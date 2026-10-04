import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { firstValueFrom, Observable, of, Subject, throwError } from 'rxjs';
import { SettingsService } from '../settings/settings.service';
import { MatReviewEditDialog } from './edit-suggestion-dialog.component';
import { LabelDto } from './labels.models';
import { LabelsService } from './labels.service';
import { EditSuggestionRequest, ReviewGroupDto, SuggestionDto } from './review.models';
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
    replaceLabels: [],
    currentLabels: [],
    labelChange: 'add',
    documentTypeLabel: null,
    documentTypeIsNew: false,
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
    replaceLabels: [],
    labelChange: 'add',
    documentTypeLabel: null,
    documentTypeIsNew: false,
  };
}

describe('EditSuggestionDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function setup(
    edit: (id: string, o: EditSuggestionRequest) => Observable<SuggestionDto> = (id) =>
      of(suggestion(id)),
    documentTypeParent: string | null = null,
  ) {
    const api = { edit: vi.fn(edit) };
    const all = documentTypeParent
      ? [...labels, { id: 'D1', name: 'Docs/Invoice', type: 'user' }]
      : labels;
    const labelsApi = { labels: vi.fn(() => of(all)), refresh: vi.fn(() => of(all)) };
    TestBed.configureTestingModule({
      providers: [
        MatReviewEditDialog,
        { provide: ReviewService, useValue: api },
        { provide: LabelsService, useValue: labelsApi },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () =>
              of({ actionLabelName: 'Act', deleteLabelName: 'Bin', documentTypeParent }),
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

  it('hides the document type without a parent and sends none', () => {
    const { api, dialog } = setup();
    dialog.editMember(suggestion('a', { documentTypeLabel: 'Docs/Invoice' })).subscribe();
    settle();
    expect(q('edit-document-type')).toBeNull();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls[0][1]).not.toHaveProperty('documentTypeLabel');
  });

  it('sends the document type only when changed, None as ""', async () => {
    const { api, dialog } = setup(undefined, 'Docs');
    dialog.editMember(suggestion('a', { documentTypeLabel: 'Docs/Invoice' })).subscribe();
    dialog.editMember(suggestion('b', { documentTypeLabel: 'Docs/Invoice' })).subscribe();
    settle();
    // The autocomplete trigger writes the input's value a microtask later.
    await Promise.resolve();
    const [first, second] = all('edit-document-type') as HTMLInputElement[];
    expect(first.value).toBe('Invoice');
    expect(all('edit-document-type-hint')[0].textContent).toContain('Existing label under Docs');
    second.value = '';
    second.dispatchEvent(new Event('input'));
    settle();
    all('edit-save').forEach((b) => b.click());
    settle();
    expect(api.edit.mock.calls[0][1]).not.toHaveProperty('documentTypeLabel');
    expect(api.edit.mock.calls[1][1].documentTypeLabel).toBe('');
  });

  it('makes free text a new child of the parent and refuses a nested one', () => {
    const { api, dialog } = setup(undefined, 'Docs');
    dialog.editGroup('news@example.com', group([suggestion('a'), suggestion('b')])).subscribe();
    settle();
    type('edit-document-type', 'A/B');
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(q('edit-document-type-error')!.textContent).toContain("One level under Docs: no '/'.");
    expect(api.edit).not.toHaveBeenCalled();
    type('edit-document-type', ' Contract ');
    expect(q('edit-document-type-hint')!.textContent).toContain('New: Contract under Docs');
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => c[1].documentTypeLabel)).toEqual([
      'Docs/Contract',
      'Docs/Contract',
    ]);
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

  it('lists current labels as Replace checkboxes and an untouched save leaves them out', () => {
    const { api, dialog } = setup();
    const member = suggestion('a', {
      currentLabels: ['Projects', 'Projects/Alpha'],
      replaceLabels: ['Projects'],
    });
    dialog.editMember(member).subscribe();
    settle();
    const boxes = all('edit-replace').map((b) => b.querySelector<HTMLInputElement>('input')!);
    expect(all('edit-replace').map((b) => b.textContent!.trim())).toEqual([
      'Projects',
      'Projects/Alpha',
    ]);
    expect(boxes.map((b) => b.checked)).toEqual([true, false]);
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls[0][1]).not.toHaveProperty('replaceLabels');
  });

  it('sends the checked labels once the Replace checkboxes change', () => {
    const { api, dialog } = setup();
    const member = suggestion('a', {
      currentLabels: ['Projects', 'Projects/Alpha'],
      replaceLabels: ['Projects'],
    });
    dialog.editMember(member).subscribe();
    settle();
    all('edit-replace').forEach((b) => b.querySelector<HTMLInputElement>('input')!.click());
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit).toHaveBeenCalledWith('a', {
      topicLabel: 'Receipts',
      needsAction: false,
      toBeDeleted: true,
      replaceLabels: ['Projects/Alpha'],
    });
  });

  it('hides the current labels block when the message has none', () => {
    const { dialog } = setup();
    dialog.editMember(suggestion('a')).subscribe();
    settle();
    expect(q('edit-current-labels')).toBeNull();
  });

  it('a group lists the union of current labels and sends each member only the ones it carries', () => {
    const { api, dialog } = setup();
    const members = [
      suggestion('a', { currentLabels: ['Projects'] }),
      suggestion('b', { currentLabels: ['Projects', 'Receipts/Old'] }),
    ];
    dialog.editGroup('news@example.com', group(members)).subscribe();
    settle();
    expect(all('edit-replace').map((b) => b.textContent!.trim())).toEqual([
      'Projects',
      'Receipts/Old',
    ]);
    all('edit-replace').forEach((b) => b.querySelector<HTMLInputElement>('input')!.click());
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => [c[0], c[1].replaceLabels])).toEqual([
      ['a', ['Projects']],
      ['b', ['Projects', 'Receipts/Old']],
    ]);
  });

  it('a group shows a label only some members replace as mixed and never adds that removal', () => {
    const { api, dialog } = setup();
    const members = [
      suggestion('a', { currentLabels: ['Projects', 'Receipts/Old'], replaceLabels: ['Projects'] }),
      suggestion('b', { currentLabels: ['Projects', 'Receipts/Old'] }),
    ];
    dialog.editGroup('news@example.com', group(members)).subscribe();
    settle();
    const boxes = all('edit-replace');
    expect(boxes.map((b) => b.querySelector<HTMLInputElement>('input')!.indeterminate)).toEqual([
      true,
      false,
    ]);
    boxes[1].querySelector<HTMLInputElement>('input')!.click();
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => [c[0], c[1].replaceLabels])).toEqual([
      ['a', ['Projects', 'Receipts/Old']],
      ['b', ['Receipts/Old']],
    ]);
  });

  it('a group ticking a mixed label applies it to every member carrying it', async () => {
    const { api, dialog } = setup();
    const members = [
      suggestion('a', { currentLabels: ['Projects'], replaceLabels: ['Projects'] }),
      suggestion('b', { currentLabels: ['Projects'] }),
    ];
    dialog.editGroup('news@example.com', group(members)).subscribe();
    settle();
    all('edit-replace')[0].querySelector<HTMLInputElement>('input')!.click();
    await Promise.resolve();
    settle();
    expect(all('edit-replace')[0].querySelector<HTMLInputElement>('input')!.indeterminate).toBe(
      false,
    );
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => [c[0], c[1].replaceLabels])).toEqual([
      ['a', undefined],
      ['b', ['Projects']],
    ]);
  });

  it("shows the server's replace label error for the member", () => {
    const { dialog } = setup(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { errors: { replaceLabels: ["Names labels the email does not carry: 'X'."] } },
          }),
      ),
    );
    dialog.editMember(suggestion('a', { currentLabels: ['X'] })).subscribe();
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(q('edit-error')!.textContent).toContain("does not carry: 'X'.");
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
  it('reports each failing member, saves the rest, and retries only the unsaved ones', async () => {
    let failB = true;
    const { api, dialog } = setup((id) =>
      id === 'b' && failB
        ? throwError(
            () => new HttpErrorResponse({ status: 409, error: { title: 'Already applied.' } }),
          )
        : of(suggestion(id)),
    );
    const members = [suggestion('a'), suggestion('b'), suggestion('c')];
    const closed = firstValueFrom(dialog.editGroup('news@example.com', group(members)));
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => c[0])).toEqual(['a', 'b', 'c']);
    expect(q('edit-error')!.textContent).toContain('1 of 3 could not be saved. 2 of 3 were saved.');
    expect(all('edit-failure').map((f) => f.textContent)).toEqual([
      expect.stringContaining('Subject b'),
    ]);
    failB = false;
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(api.edit.mock.calls.map((c) => c[0])).toEqual(['a', 'b', 'c', 'b']);
    expect(await closed).toBe(true);
  });

  it('returns true when closed after a partial save', async () => {
    const { dialog } = setup((id) =>
      id === 'b'
        ? throwError(() => new HttpErrorResponse({ status: 409, error: { title: 'Conflict' } }))
        : of(suggestion(id)),
    );
    const closed = firstValueFrom(
      dialog.editGroup('news@example.com', group([suggestion('a'), suggestion('b')])),
    );
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    q<HTMLButtonElement>('edit-cancel')!.click();
    expect(await closed).toBe(true);
  });

  it('cannot be closed while a save is running', async () => {
    const pending = new Subject<SuggestionDto>();
    const { dialog } = setup(() => pending);
    let result: boolean | undefined;
    const closed = firstValueFrom(dialog.editMember(suggestion('a')));
    void closed.then((r) => (result = r));
    settle();
    q<HTMLButtonElement>('edit-save')!.click();
    settle();
    expect(q<HTMLButtonElement>('edit-cancel')!.disabled).toBe(true);
    document
      .querySelector('mat-dialog-container')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
    settle();
    await Promise.resolve();
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
    expect(result).toBeUndefined();
    pending.next(suggestion('a'));
    pending.complete();
    settle();
    expect(await closed).toBe(true);
  });
});
