import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, of } from 'rxjs';
import {
  bulkApproveData,
  newLabelExclusionText,
  openBulkApprove,
} from './bulk-approve-dialog.component';
import { ReviewSenderDetailDto, SuggestionDto } from './review.models';
import { ReviewService } from './review.service';

describe('BulkApproveDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function open(senderAddress: string | null, newLabelPending = 0) {
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

  it('says how many new-label suggestions are excluded, before and after approving', () => {
    open('news@example.com', 3);
    settle();
    const text = 'are excluded while the taxonomy is locked';
    expect(q('[data-testid="bulk-new-label-excluded"]').textContent).toContain(
      `3 suggestions with new labels ${text}`,
    );
    q<HTMLButtonElement>('[data-testid="bulk-submit"]').click();
    settle();
    expect(q('[data-testid="bulk-new-label-excluded"]').textContent).toContain(text);
  });

  it('words the exclusion for one and for none', () => {
    expect(newLabelExclusionText(0)).toBeNull();
    expect(newLabelExclusionText(1)).toBe(
      '1 suggestion with a new label is excluded while the taxonomy is locked.',
    );
  });

  it('counts the listed newLabelPending members and falls back to the threshold maximum', () => {
    const member = (newLabelPending: boolean) => ({ newLabelPending }) as SuggestionDto;
    const detail = {
      groups: [{ members: [member(true), member(false)] }, { members: [member(true)] }],
    } as ReviewSenderDetailDto;
    const data = bulkApproveData(null, 'news@example.com', detail);
    expect(data.newLabelPending).toBe(2);
    expect(data.threshold).toBe(data.max);
    expect(bulkApproveData({ bulkApproveThreshold: 0.9 }, null, null)).toMatchObject({
      threshold: 0.9,
      newLabelPending: 0,
    });
  });
});
