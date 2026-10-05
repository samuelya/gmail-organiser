import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, of } from 'rxjs';
import {
  bulkApproveData,
  exclusionFor,
  NEW_LABEL_EXCLUDED,
  newLabelExclusionText,
  openBulkApprove,
} from './bulk-approve-dialog.component';
import { ReviewSenderDetailDto, SuggestionDto } from './review.models';
import { ReviewService } from './review.service';

describe('BulkApproveDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function open(
    senderAddress: string | null,
    newLabelPending: readonly SuggestionDto[] | null = [],
    taxonomyLocked = false,
  ) {
    const api = {
      bulkApprove: vi.fn(() => of({ approved: 5, skippedProtected: 2, skippedIds: ['x', 'y'] })),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: ReviewService, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const closed = firstValueFrom(
      openBulkApprove(TestBed.inject(MatDialog), {
        threshold: 0.85,
        min: 0.5,
        max: 1,
        step: 0.01,
        senderAddress,
        taxonomyLocked,
        newLabelPending,
      }),
    );
    return { api, closed };
  }

  const q = <T extends HTMLElement>(selector: string) =>
    document.querySelector<T>(`mat-dialog-container ${selector}`)!;
  const settle = () => TestBed.tick();

  it('prefills the threshold and sends the default payload', async () => {
    const { api } = open('news@example.com');
    settle();
    expect(q('[data-testid="bulk-threshold-value"]').textContent).toContain('85%');
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(api.bulkApprove).toHaveBeenCalledWith({ threshold: 0.85, includeDerived: false });
  });

  it('sends derived and the sender scope, shows the result and reports a change', async () => {
    const { api, closed } = open('news@example.com');
    settle();
    q<HTMLInputElement>('[data-testid="bulk-include-derived"] input').click();
    q<HTMLInputElement>('[data-testid="bulk-sender-only"] input').click();
    settle();
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(api.bulkApprove).toHaveBeenCalledWith({
      threshold: 0.85,
      includeDerived: true,
      senderAddress: 'news@example.com',
    });
    const result = q('[data-testid="bulk-result"]').textContent!;
    expect(result).toContain('Approved 5 suggestions');
    expect(result).toContain('2 left pending because their message is protected');
    q<HTMLButtonElement>('[data-testid="bulk-close"]').click();
    expect(await closed).toBe(true);
  });

  it('offers no sender scope without a selected sender', () => {
    open(null);
    settle();
    expect(document.querySelector('[data-testid="bulk-sender-only"]')).toBeNull();
    expect(document.querySelector('[data-testid="bulk-new-label-excluded"]')).toBeNull();
  });

  const pending = (confidence: number, source: SuggestionDto['source'] = 'llm') =>
    ({ newLabelPending: true, status: 'pending', confidence, source }) as SuggestionDto;

  it('gives a count only for this sender with every suggestion known, before and after approving', () => {
    open('news@example.com', [pending(0.9), pending(0.95), pending(0.99)], true);
    settle();
    const excluded = () => q('[data-testid="bulk-new-label-excluded"]').textContent!;
    // All senders: B's and C's new-label suggestions are unknown, so no number.
    expect(excluded()).toContain(NEW_LABEL_EXCLUDED);
    expect(excluded()).not.toMatch(/\d/);
    q<HTMLInputElement>('[data-testid="bulk-sender-only"] input').click();
    settle();
    expect(excluded()).toContain('3 suggestions with new labels are excluded');
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(excluded()).toContain('3 suggestions with new labels are excluded');
  });

  it('shows no number when the sender may have suggestions on other pages', () => {
    open('news@example.com', null, true);
    settle();
    q<HTMLInputElement>('[data-testid="bulk-sender-only"] input').click();
    settle();
    expect(q('[data-testid="bulk-new-label-excluded"]').textContent).toContain(NEW_LABEL_EXCLUDED);
  });

  it('says nothing about new labels while the taxonomy is unlocked', () => {
    open('news@example.com', [pending(0.9)], false);
    settle();
    expect(document.querySelector('[data-testid="bulk-new-label-excluded"]')).toBeNull();
  });

  it('counts like the request: threshold and derived/memory sources', () => {
    const data = bulkApproveData(
      { bulkApproveThreshold: 0.85, taxonomyLocked: true },
      'news@example.com',
      null,
      true,
    );
    const known = {
      ...data,
      newLabelPending: [pending(0.8), pending(0.9), pending(0.95, 'derived')],
    };
    const form = { threshold: 0.85, includeDerived: false, senderOnly: true };
    expect(exclusionFor(known, form)).toBe(newLabelExclusionText(1));
    expect(exclusionFor(known, { ...form, includeDerived: true })).toBe(newLabelExclusionText(2));
    expect(exclusionFor(known, { ...form, threshold: 0.99 })).toBeNull();
    expect(exclusionFor(known, { ...form, senderOnly: false })).toBe(NEW_LABEL_EXCLUDED);
  });

  it('words the exclusion for one and for none', () => {
    expect(newLabelExclusionText(0)).toBeNull();
    expect(newLabelExclusionText(1)).toBe(
      '1 suggestion with a new label is excluded while the taxonomy is locked.',
    );
  });

  it('knows every new-label suggestion only for one unfiltered page without truncated groups', () => {
    const member = (newLabelPending: boolean) =>
      ({ newLabelPending, status: 'pending' }) as SuggestionDto;
    const detail = (totalGroups: number, truncated = false) =>
      ({
        groups: [
          { members: [member(true), member(false)], truncated },
          { members: [member(true)], truncated: false },
        ],
        totalGroups,
      }) as ReviewSenderDetailDto;
    const data = bulkApproveData(null, 'news@example.com', detail(2), true);
    expect(data.newLabelPending).toHaveLength(2);
    expect(data.threshold).toBe(data.max);
    expect(data.taxonomyLocked).toBe(false);
    expect(bulkApproveData(null, 'news@example.com', detail(40), true).newLabelPending).toBeNull();
    expect(
      bulkApproveData(null, 'news@example.com', detail(2, true), true).newLabelPending,
    ).toBeNull();
    expect(bulkApproveData(null, 'news@example.com', detail(2), false).newLabelPending).toBeNull();
    expect(bulkApproveData({ bulkApproveThreshold: 0.9 }, null, null, true)).toMatchObject({
      threshold: 0.9,
      newLabelPending: null,
    });
  });
});
