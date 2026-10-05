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
import { ReviewService } from './review.service';

describe('BulkApproveDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function open(senderAddress: string | null, taxonomyLocked = false, excludedNewLabel = 0) {
    const api = {
      bulkApprove: vi.fn(() =>
        of({ approved: 5, skippedProtected: 2, skippedIds: ['x', 'y'], excludedNewLabel }),
      ),
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

  const excluded = () =>
    document.querySelector('[data-testid="bulk-new-label-excluded"]')?.textContent ?? null;

  it('says new labels are excluded before approving, then shows the count the API excluded', () => {
    open('news@example.com', true, 3);
    settle();
    expect(excluded()).toContain(NEW_LABEL_EXCLUDED);
    expect(excluded()).not.toMatch(/\d/);
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(excluded()).toContain(
      '3 suggestions with new labels were excluded while the taxonomy is locked.',
    );
  });

  it('shows no excluded line after approving when the API excluded none', () => {
    open('news@example.com', true, 0);
    settle();
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(q('[data-testid="bulk-result"]')).toBeTruthy();
    expect(excluded()).toBeNull();
  });

  it('says nothing about new labels while the taxonomy is unlocked', () => {
    open('news@example.com', false);
    settle();
    expect(excluded()).toBeNull();
  });

  it('takes the line from the response only', () => {
    const data = bulkApproveData({ bulkApproveThreshold: 0.85, taxonomyLocked: true }, null);
    const response = { approved: 1, skippedProtected: 0, skippedIds: [], excludedNewLabel: 2 };
    expect(exclusionFor(data, null)).toBe(NEW_LABEL_EXCLUDED);
    expect(exclusionFor(data, response)).toBe(newLabelExclusionText(2));
    expect(exclusionFor(data, { ...response, excludedNewLabel: 0 })).toBeNull();
    expect(exclusionFor({ ...data, taxonomyLocked: false }, null)).toBeNull();
  });

  it('words the exclusion for one and for none', () => {
    expect(newLabelExclusionText(0)).toBeNull();
    expect(newLabelExclusionText(1)).toBe(
      '1 suggestion with a new label was excluded while the taxonomy is locked.',
    );
  });

  it('takes the threshold and lock from settings', () => {
    const data = bulkApproveData(null, 'news@example.com');
    expect(data.threshold).toBe(data.max);
    expect(data.taxonomyLocked).toBe(false);
    expect(bulkApproveData({ bulkApproveThreshold: 0.9 }, null)).toMatchObject({
      threshold: 0.9,
      senderAddress: null,
    });
  });
});
