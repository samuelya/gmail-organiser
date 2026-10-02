import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, of } from 'rxjs';
import { openBulkApprove } from './bulk-approve-dialog.component';
import { ReviewService } from './review.service';

describe('BulkApproveDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function open(senderAddress: string | null) {
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
  });
});
