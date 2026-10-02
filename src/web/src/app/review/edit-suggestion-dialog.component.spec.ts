import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { firstValueFrom, Observable, of, Subject, throwError } from 'rxjs';
import { SettingsService } from '../settings/settings.service';
import { MatReviewEditDialog } from './edit-suggestion-dialog.component';
import { LabelDto } from './labels.models';
import { LabelsService } from './labels.service';
import { ReviewGroupDto, ReviewOutcome, SuggestionDto } from './review.models';
import { ReviewService } from './review.service';

const labels: LabelDto[] = [
  { id: 'INBOX', name: 'INBOX', type: 'system' },
  { id: 'L1', name: 'Projects', type: 'user' },
  { id: 'L2', name: 'Projects/Alpha', type: 'user' },
  { id: 'L3', name: 'Receipts', type: 'user' },
];

function suggestion(id: string, overrides: Partial<SuggestionDto> = {}): SuggestionDto {
  return {
    id,
    messageId: `m-${id}`,
    subject: `Subject ${id}`,
    date: '2026-01-01T00:00:00Z',
    snippet: null,
    source: 'llm',
    topicLabel: 'Receipts',
    isNewLabel: false,
    needsAction: false,
    toBeDeleted: true,
    unsubscribeSuggested: false,
    confidence: 0.9,
    reason: 'synthetic',
    status: 'pending',
    edited: false,
    protected: false,
    ...overrides,
  };
}

function group(members: SuggestionDto[], truncated = false): ReviewGroupDto {
  return {
    groupKey: 'g1',
    display: 'Monthly statements',
    size: members.length,
    llmCount: members.length,
    derivedCount: 0,
    memoryCount: 0,
    topicLabel: 'Receipts',
    needsAction: false,
    toBeDeleted: true,
    mixed: false,
    confidenceMin: 0.9,
    confidenceMax: 0.9,
    reason: 'synthetic',
    members,
    truncated,
  };
}

describe('EditSuggestionDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function setup(
    edit: (id: string, o: ReviewOutcome) => Observable<SuggestionDto> = (id) => of(suggestion(id)),
  ) {
    const api = { edit: vi.fn(edit) };
    const labelsApi = { labels: vi.fn(() => of(labels)), refresh: vi.fn(() => of(labels)) };
    TestBed.configureTestingModule({
      providers: [
        MatReviewEditDialog,
        { provide: ReviewService, useValue: api },
        { provide: LabelsService, useValue: labelsApi },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () => of({ actionLabelName: 'Act', deleteLabelName: 'Bin' }),
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    return { api, labelsApi, dialog: TestBed.inject(MatReviewEditDialog) };
  }

  const q = <T extends HTMLElement>(id: string) =>
    document.querySelector<T>(`mat-dialog-container [data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(
      document.querySelectorAll<HTMLElement>(`mat-dialog-container [data-testid="${id}"]`),
    );
  const settle = () => TestBed.tick();
  const type = (id: string, value: string) => {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    settle();
  };

  it('shows the current outcome and the label tree built from user labels', () => {
    const { dialog } = setup();
    dialog.editMember(suggestion('a')).subscribe();
    settle();
    expect(q('edit-current')!.textContent).toContain('Receipts');
    expect(q('edit-current')!.textContent).toContain('Bin');
    expect(q<HTMLInputElement>('edit-label')!.value).toBe('Receipts');
    expect(all('label-node').map((n) => n.dataset['path'])).toEqual(['Projects', 'Receipts']);
    expect(q('edit-placement')!.textContent).toContain('Existing label');
  });

  it('filters the tree and picks a nested label', async () => {
    const { api, dialog } = setup();
    const closed = firstValueFrom(dialog.editMember(suggestion('a')));
    settle();
    type('label-filter', 'alp');
    expect(all('label-node').map((n) => n.dataset['path'])).toEqual(['Projects', 'Projects/Alpha']);
    all('label-node')[1].click();
    settle();
    expect(q<HTMLInputElement>('edit-label')!.value).toBe('Projects/Alpha');
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit).toHaveBeenCalledWith('a', {
      topicLabel: 'Projects/Alpha',
      needsAction: false,
      toBeDeleted: true,
    });
    expect(await closed).toBe(true);
  });

  it('validates a new path inline and previews where it lands', () => {
    const { api, dialog } = setup();
    dialog.editMember(suggestion('a')).subscribe();
    settle();
    type('edit-label', 'Projects/Beta/2026');
    expect(q('edit-placement')!.textContent).toContain('New: Beta/2026 under Projects');
    type('edit-label', 'a/b/c/d/e/f');
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(q('edit-label-error')!.textContent).toContain('At most five levels.');
    expect(api.edit).not.toHaveBeenCalled();
  });

  it('disables To-Be-Deleted for a protected member and never sends it', () => {
    const { api, dialog } = setup();
    dialog.editMember(suggestion('a', { protected: true })).subscribe();
    settle();
    const toggle = q('edit-to-be-deleted')!.querySelector<HTMLButtonElement>('button')!;
    expect(toggle.disabled).toBe(true);
    expect(toggle.getAttribute('aria-checked')).toBe('false');
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit).toHaveBeenCalledWith('a', {
      topicLabel: 'Receipts',
      needsAction: false,
      toBeDeleted: false,
    });
  });

  it('saves every editable group member in order, keeping protected ones off deletion', async () => {
    const { api, dialog } = setup();
    const members = [
      suggestion('a'),
      suggestion('b', { protected: true }),
      suggestion('c', { status: 'applied' }),
    ];
    const closed = firstValueFrom(dialog.editGroup('news@example.com', group(members, true)));
    settle();
    expect(q('edit-protected-note')!.textContent).toContain('1 protected member keeps Bin off');
    expect(q('edit-truncated')).not.toBeNull();
    q('edit-needs-action')!.querySelector<HTMLButtonElement>('button')!.click();
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls).toEqual([
      ['a', { topicLabel: 'Receipts', needsAction: true, toBeDeleted: true }],
      ['b', { topicLabel: 'Receipts', needsAction: true, toBeDeleted: false }],
    ]);
    expect(await closed).toBe(true);
  });

  it('shows progress for groups over 20 while saving sequentially', () => {
    const pending = new Map<string, Subject<SuggestionDto>>();
    const { api, dialog } = setup((id) => {
      const s = new Subject<SuggestionDto>();
      pending.set(id, s);
      return s;
    });
    const members = Array.from({ length: 21 }, (_, i) => suggestion(`s${i}`));
    dialog.editGroup('news@example.com', group(members)).subscribe();
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit).toHaveBeenCalledTimes(1);
    pending.get('s0')!.next(suggestion('s0'));
    pending.get('s0')!.complete();
    settle();
    expect(api.edit).toHaveBeenCalledTimes(2);
    expect(q('edit-progress')).not.toBeNull();
    expect(document.querySelector('mat-dialog-container')!.textContent).toContain('1 of 21 saved');
  });

  it("shows the server's label error inline and stays open", () => {
    const { dialog } = setup(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { title: 'Invalid', errors: { topicLabel: ['Server says no.'] } },
          }),
      ),
    );
    dialog.editMember(suggestion('a')).subscribe();
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(q('edit-label-error')!.textContent).toContain('Server says no.');
    expect(q('edit-error')).toBeNull();
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
  });

  it('cancel changes nothing', async () => {
    const { api, dialog } = setup();
    const closed = firstValueFrom(dialog.editMember(suggestion('a')));
    settle();
    type('edit-label', 'Other');
    q<HTMLButtonElement>('edit-cancel')!.click();
    expect(await closed).toBe(false);
    expect(api.edit).not.toHaveBeenCalled();
  });
});
