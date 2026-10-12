import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { LabelsService } from '../review/labels.service';
import { SettingsService } from '../settings/settings.service';
import {
  FilterPreviewDialog,
  FilterPreviewDialogData,
  PREVIEW_DEBOUNCE_MS,
} from './filter-preview-dialog.component';
import { FilterDto, FilterPreviewDto, FilterRequest } from './rules.models';
import { RulesService } from './rules.service';

const suggested: FilterRequest = {
  criteria: {
    from: 'news@example.com',
    to: null,
    subject: null,
    query: null,
    negatedQuery: null,
    hasAttachment: null,
    excludeChats: null,
    size: null,
    sizeComparison: null,
  },
  action: { addLabelNames: ['Topic/Alpha'], skipInbox: true, markRead: false },
};

const preview = (over: Partial<FilterPreviewDto> = {}): FilterPreviewDto => ({
  criteria: suggested.criteria,
  criteriaSummary: 'from:news@example.com',
  query: 'from:news@example.com',
  localMatches: 12,
  gmailEstimate: 40,
  action: {
    addLabels: [],
    removeLabelIds: ['INBOX'],
    skipInbox: true,
    markRead: false,
    forwards: false,
  },
  createsLabels: ['Topic/Alpha'],
  warnings: [],
  ...over,
});

const created = { id: 'f-new' } as FilterDto;

describe('FilterPreviewDialog', () => {
  let rules: { preview: ReturnType<typeof vi.fn>; create: ReturnType<typeof vi.fn> };
  let ref: { close: ReturnType<typeof vi.fn>; disableClose: boolean };

  async function render(data: FilterPreviewDialogData, archive: string[] = []) {
    rules = { preview: vi.fn(() => of(preview())), create: vi.fn(() => of(created)) };
    ref = { close: vi.fn(), disableClose: false };
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: ref },
        { provide: RulesService, useValue: rules },
        {
          provide: LabelsService,
          useValue: {
            labels: () => of([{ id: 'L1', name: 'Topic', type: 'user' }]),
            refresh: () => of([]),
          },
        },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () =>
              of({ appsScript: { rules: archive.map((label) => ({ label, days: 7 })) } }),
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(FilterPreviewDialog);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, el, q, settle };
  }

  afterEach(() => vi.useRealTimers());

  it('previews the suggestion at once and creates it', async () => {
    const { q } = await render({ request: suggested, from: 'news@example.com' });
    expect(rules.preview).toHaveBeenCalledWith(suggested);
    expect(q('preview-local')!.textContent).toContain('12 fetched messages match');
    expect(q('preview-gmail')!.textContent).toContain('About 40 in Gmail');
    expect(q('preview-creates')!.textContent).toContain('Topic/Alpha');
    expect(q('preview-archive')).toBeNull();
    q('preview-create')!.click();
    expect(rules.create).toHaveBeenCalledWith(suggested);
    expect(ref.close).toHaveBeenCalledWith(created);
  });

  it('shows why there is no local count and Gmail warnings', async () => {
    const { q, settle } = await render({ request: suggested, from: '' });
    rules.preview.mockReturnValue(
      of(
        preview({ localMatches: null, gmailEstimate: null, warnings: ['Gmail is not connected.'] }),
      ),
    );
    q('preview-mark-read')!.querySelector('button')!.click();
    await new Promise((r) => setTimeout(r, PREVIEW_DEBOUNCE_MS + 50));
    await settle();
    expect(rules.preview).toHaveBeenCalledTimes(2);
    expect(rules.preview.mock.calls[1][0].action.markRead).toBe(true);
    expect(q('preview-local')!.textContent).toContain('not counted');
    expect(q('preview-warning')!.textContent).toContain('Gmail is not connected.');
  });

  it('debounces typing into one preview', async () => {
    const { fixture } = await render({ request: suggested, from: '' });
    vi.useFakeTimers();
    const subject = fixture.componentInstance.form.controls.subject;
    subject.setValue('a');
    subject.setValue('ab');
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS - 1);
    expect(rules.preview).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1);
    expect(rules.preview).toHaveBeenCalledTimes(2);
    expect(rules.preview.mock.calls[1][0].criteria.subject).toBe('ab');
  });

  it('opens with just from, and asks for an action before previewing', async () => {
    const { q } = await render({ request: null, from: 'shop@example.com' });
    expect((q('preview-from') as HTMLInputElement).value).toBe('shop@example.com');
    expect(rules.preview).not.toHaveBeenCalled();
    expect(q('preview-problem')!.textContent).toContain('at least one action');
    expect((q('preview-create') as HTMLButtonElement).disabled).toBe(true);
  });

  it('adds the archive-rule label when picked', async () => {
    const { fixture, q, settle } = await render({ request: suggested, from: '' }, [
      'Archive/Weekly',
      'Archive/Weekly',
    ]);
    expect(q('preview-archive')).not.toBeNull();
    expect(fixture.componentInstance.archiveLabels()).toEqual(['Archive/Weekly']);
    fixture.componentInstance.form.controls.archiveLabel.setValue('Archive/Weekly');
    await settle();
    q('preview-create')!.click();
    expect(rules.create.mock.calls[0][0].action.addLabelNames).toEqual([
      'Topic/Alpha',
      'Archive/Weekly',
    ]);
  });

  it('pre-fills additional labels apart from the archive label and creates them all', async () => {
    const names = ['Topic/Alpha', 'Archive/Weekly', 'Extra/One', 'Extra/Two'];
    const request = { ...suggested, action: { ...suggested.action, addLabelNames: names } };
    const { fixture, el, q } = await render({ request, from: '' }, ['Archive/Weekly']);
    const form = fixture.componentInstance.form.controls;
    expect(form.archiveLabel.value).toBe('Archive/Weekly');
    expect(form.extraLabels.value).toEqual(['Extra/One', 'Extra/Two']);
    const chips = el.querySelectorAll('[data-testid="extra-label-chip"]');
    expect([...chips].map((c) => c.textContent!.trim().split(/\s/)[0])).toEqual([
      'Extra/One',
      'Extra/Two',
    ]);
    q('preview-create')!.click();
    expect(rules.create.mock.calls[0][0].action.addLabelNames).toEqual(names);
  });

  it('disables additional labels without a label path and leaves them out of the request', async () => {
    const request = {
      ...suggested,
      action: { ...suggested.action, addLabelNames: ['Topic/Alpha', 'Extra/One'] },
    };
    const { fixture, q, settle } = await render({ request, from: '' });
    const form = fixture.componentInstance.form.controls;
    form.labelPath.setValue('');
    await settle();
    expect(form.extraLabels.disabled).toBe(true);
    q('preview-create')!.click();
    expect(rules.create.mock.calls[0][0].action.addLabelNames).toEqual([]);
    form.labelPath.setValue('Topic/Beta');
    expect(form.extraLabels.enabled).toBe(true);
  });

  it('refuses an additional label equal to the label path', async () => {
    const { fixture, q, settle } = await render({ request: suggested, from: '' });
    fixture.componentInstance.form.controls.extraLabels.setValue(['topic/alpha']);
    await settle();
    expect(fixture.componentInstance.form.invalid).toBe(true);
    q('preview-create')!.click();
    expect(rules.create).not.toHaveBeenCalled();
  });

  it('keeps previewing the added chips while a label is typed, and blocks only Create', async () => {
    const request = {
      ...suggested,
      action: { ...suggested.action, addLabelNames: ['Topic/Alpha', 'Extra/One'] },
    };
    const { q, settle } = await render({ request, from: '' });
    vi.useFakeTimers();
    const input = q('extra-labels-input') as HTMLInputElement;
    for (const text of ['E', 'Ex', 'Ext']) {
      input.value = text;
      input.dispatchEvent(new Event('input'));
      await settle();
    }
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    await settle();
    expect(rules.preview).toHaveBeenCalledTimes(1);
    expect(q('preview-creates')).not.toBeNull();
    expect(q('preview-problem')).toBeNull();
    q('preview-create')!.click();
    expect(rules.create).not.toHaveBeenCalled();
  });

  it('drops an additional label that is picked as the archive label', async () => {
    const request = {
      ...suggested,
      action: { ...suggested.action, addLabelNames: ['Topic/Alpha', 'archive/weekly'] },
    };
    const { fixture, q, settle } = await render({ request, from: '' }, ['Archive/Weekly', 'Other']);
    const form = fixture.componentInstance.form.controls;
    expect(form.archiveLabel.value).toBe('');
    form.archiveLabel.setValue('Archive/Weekly');
    await settle();
    expect(form.extraLabels.value).toEqual([]);
    expect(fixture.componentInstance.form.valid).toBe(true);
    q('preview-create')!.click();
    expect(rules.create.mock.calls[0][0].action.addLabelNames).toEqual([
      'Topic/Alpha',
      'Archive/Weekly',
    ]);
  });

  it("says a clash is with the filter's label", async () => {
    const { fixture, q, settle } = await render({ request: suggested, from: '' });
    fixture.componentInstance.form.controls.extraLabels.setValue(['topic/alpha']);
    await settle();
    expect(q('extra-labels-error')!.textContent).toContain(
      "is already the filter's label or archive label",
    );
  });

  it('pre-fills at most the field limit of additional labels and says how many were left out', async () => {
    const extras = ['A/1', 'A/2', 'A/3', 'A/4', 'A/5', 'A/6', 'A/7'];
    const request = {
      ...suggested,
      action: { ...suggested.action, addLabelNames: ['Topic/Alpha', 'Archive/Weekly', ...extras] },
    };
    const { fixture, q } = await render({ request, from: '' }, ['Archive/Weekly']);
    const form = fixture.componentInstance.form;
    expect(form.controls.extraLabels.value).toEqual(extras.slice(0, 5));
    expect(form.valid).toBe(true);
    expect(q('preview-extras-dropped')!.textContent).toContain('2 more suggested labels were');
  });

  it('stays open with the reason when Gmail refuses the create', async () => {
    const { q, settle } = await render({ request: suggested, from: '' });
    rules.create.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { title: 'Gmail refused the change', detail: 'Filter already exists' },
          }),
      ),
    );
    q('preview-create')!.click();
    await settle();
    expect(ref.close).not.toHaveBeenCalled();
    expect(q('preview-error')!.textContent).toContain('Filter already exists');
    expect((q('preview-create') as HTMLButtonElement).disabled).toBe(false);
  });
});
