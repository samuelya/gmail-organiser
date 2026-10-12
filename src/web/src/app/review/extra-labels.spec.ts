import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { Observable, of, throwError } from 'rxjs';
import { SettingsService } from '../settings/settings.service';
import { MatReviewEditDialog } from './edit-suggestion-dialog.component';
import { GroupCard } from './group-card.component';
import { LabelDto } from './labels.models';
import { LabelsService } from './labels.service';
import { EditSuggestionRequest, outcomeOf, ReviewGroupDto, SuggestionDto } from './review.models';
import { ReviewService } from './review.service';

const labels: LabelDto[] = [
  { id: 'L1', name: 'Topic/Alpha', type: 'user' },
  { id: 'L2', name: 'Travel', type: 'user' },
  { id: 'L3', name: 'Projects', type: 'user' },
];

const member = (id: string, over: Partial<SuggestionDto> = {}): SuggestionDto => ({
  id,
  messageId: `m-${id}`,
  subject: `Subject ${id}`,
  date: '2026-01-01T00:00:00Z',
  snippet: null,
  source: 'llm',
  topicLabel: 'Topic/Alpha',
  isNewLabel: false,
  needsAction: false,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.9,
  reason: '',
  status: 'pending',
  edited: false,
  protected: false,
  replaceLabels: [],
  currentLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

const group = (over: Partial<ReviewGroupDto> = {}): ReviewGroupDto => ({
  groupKey: 'g1',
  display: 'Synthetic group',
  size: 1,
  llmCount: 1,
  derivedCount: 0,
  memoryCount: 0,
  topicLabel: 'Topic/Alpha',
  needsAction: false,
  toBeDeleted: false,
  mixed: false,
  confidenceMin: 0.9,
  confidenceMax: 0.9,
  reason: '',
  members: [member('a')],
  truncated: false,
  replaceLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

describe('additional labels payloads', () => {
  it("group approve's outcome carries the card's additional labels, none when missing", () => {
    expect(outcomeOf(group({ extraLabels: ['Travel'] })).extraLabels).toEqual(['Travel']);
    expect(outcomeOf(group()).extraLabels).toEqual([]);
  });
});

describe('additional label chips', () => {
  it('shows each extra after the topic on the card and the member, marking new ones', async () => {
    const extras = { extraLabels: ['Ideas', 'Travel'], extraLabelsNew: ['Ideas'] };
    const fixture = TestBed.createComponent(GroupCard);
    fixture.componentRef.setInput('group', group({ ...extras, members: [member('a', extras)] }));
    fixture.componentRef.setInput('senderAddress', 'news@example.com');
    fixture.componentRef.setInput('labels', { action: 'Act', delete: 'Bin' });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLElement>('[data-testid="group-expand"]')!.click();
    await fixture.whenStable();
    const chips = [...el.querySelectorAll<HTMLElement>('[data-testid="extra-label"]')];
    expect(chips.map((c) => c.textContent!.trim())).toEqual([
      'Ideas new',
      'Travel',
      'Ideas new',
      'Travel',
    ]);
  });
});

describe('additional labels in the edit dialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function setup(
    edit: (id: string, o: EditSuggestionRequest) => Observable<SuggestionDto> = (id) =>
      of(member(id)),
  ) {
    const api = { edit: vi.fn(edit) };
    TestBed.configureTestingModule({
      providers: [
        MatReviewEditDialog,
        { provide: ReviewService, useValue: api },
        { provide: LabelsService, useValue: { labels: () => of(labels) } },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () =>
              of({ actionLabelName: 'Act', deleteLabelName: 'Bin', documentTypeParent: null }),
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    return { api, dialog: TestBed.inject(MatReviewEditDialog) };
  }

  const q = <T extends HTMLElement>(id: string) =>
    document.querySelector<T>(`mat-dialog-container [data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(
      document.querySelectorAll<HTMLElement>(`mat-dialog-container [data-testid="${id}"]`),
    );
  const settle = () => TestBed.tick();
  const save = () => {
    q('edit-save')!.click();
    settle();
  };

  it('pre-fills from the member and leaves extraLabels out of an untouched save', () => {
    const { api, dialog } = setup();
    dialog.editMember(member('a', { extraLabels: ['Travel'] })).subscribe();
    settle();
    expect(all('extra-label-chip').map((c) => c.textContent)).toEqual([
      expect.stringContaining('Travel'),
    ]);
    save();
    expect(api.edit.mock.calls[0][1]).not.toHaveProperty('extraLabels');
  });

  it('sends [] when every extra is removed, to every group member', () => {
    const { api, dialog } = setup();
    const members = [member('a'), member('b')];
    dialog.editGroup('news@example.com', group({ members, extraLabels: ['Travel'] })).subscribe();
    settle();
    q('extra-label-remove')!.click();
    settle();
    save();
    expect(api.edit.mock.calls.map((c) => c[1].extraLabels)).toEqual([[], []]);
  });

  it('blocks saving while an extra equals the topic or a flag label', () => {
    const { api, dialog } = setup();
    dialog.editMember(member('a', { extraLabels: ['Act'] })).subscribe();
    settle();
    expect(q('extra-labels-error')!.textContent).toContain("'Act'");
    save();
    expect(api.edit).not.toHaveBeenCalled();
  });

  it("shows the server's extraLabels error under the field", () => {
    const { dialog } = setup(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { title: 'Invalid', errors: { extraLabels: ["Entry 1 'Travel': no."] } },
          }),
      ),
    );
    dialog.editMember(member('a', { extraLabels: ['Travel', 'Projects'] })).subscribe();
    settle();
    q('extra-label-remove')!.click();
    settle();
    save();
    expect(q('extra-labels-error')!.textContent).toContain("Entry 1 'Travel': no.");
    expect(q('edit-error')).toBeNull();
  });
});
