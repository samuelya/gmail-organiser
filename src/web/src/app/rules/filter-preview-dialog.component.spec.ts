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
