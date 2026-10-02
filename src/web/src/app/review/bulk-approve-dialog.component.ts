import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSliderModule } from '@angular/material/slider';
import { map, Observable } from 'rxjs';
import { BulkApproveRequest, BulkApproveResponse, percent } from './review.models';
import { ReviewService } from './review.service';

export interface BulkApproveDialogData {
  /** Prefilled from the `bulkApproveThreshold` setting. */
  threshold: number;
  min: number;
  max: number;
  step: number;
  /** The selected sender, offered as "this sender only". */
  senderAddress: string | null;
}

/** "Bulk approve…": threshold, derived/memory and sender scope; shows what the API approved and skipped. */
@Component({
  selector: 'app-bulk-approve-dialog',
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatSliderModule,
    ReactiveFormsModule,
  ],
  template: `
    <h2 mat-dialog-title>Bulk approve</h2>
    <mat-dialog-content>
      @if (result(); as r) {
        <p class="m-0" data-testid="bulk-result">
          Approved {{ r.approved }} {{ r.approved === 1 ? 'suggestion' : 'suggestions' }}.
          @if (r.skippedProtected > 0) {
            {{ r.skippedProtected }} left pending because their message is protected and they would
            mark it for deletion.
          }
        </p>
      } @else {
        <form [formGroup]="form" class="flex flex-col gap-3" (ngSubmit)="submit()">
          <label id="bulk-threshold-label" for="bulk-threshold"
            >Approve pending model suggestions with confidence of at least
            <strong data-testid="bulk-threshold-value">{{ thresholdText() }}</strong></label
          >
          <mat-slider [min]="data.min" [max]="data.max" [step]="data.step" discrete>
            <input
              matSliderThumb
              id="bulk-threshold"
              formControlName="threshold"
              aria-labelledby="bulk-threshold-label"
              data-testid="bulk-threshold"
            />
          </mat-slider>
          <mat-checkbox formControlName="includeDerived" data-testid="bulk-include-derived"
            >Include derived and memory suggestions</mat-checkbox
          >
          @if (data.senderAddress) {
            <mat-checkbox formControlName="senderOnly" data-testid="bulk-sender-only"
              >Only {{ data.senderAddress }}</mat-checkbox
            >
          }
        </form>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (result()) {
        <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="bulk-close">
          Close
        </button>
      } @else {
        <button mat-button type="button" [mat-dialog-close]="false">Cancel</button>
        <button
          mat-flat-button
          type="button"
          [disabled]="sending()"
          (click)="submit()"
          data-testid="bulk-submit"
        >
          Approve
        </button>
      }
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BulkApproveDialog {
  readonly data = inject<BulkApproveDialogData>(MAT_DIALOG_DATA);
  private readonly review = inject(ReviewService);
  private readonly destroyRef = inject(DestroyRef);

  readonly form = new FormGroup({
    threshold: new FormControl(this.data.threshold, { nonNullable: true }),
    includeDerived: new FormControl(false, { nonNullable: true }),
    senderOnly: new FormControl(false, { nonNullable: true }),
  });
  readonly sending = signal(false);
  readonly result = signal<BulkApproveResponse | null>(null);
  readonly thresholdValue = signal(this.data.threshold);

  constructor() {
    this.form.controls.threshold.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((v) => this.thresholdValue.set(v));
  }

  thresholdText(): string {
    return percent(this.thresholdValue());
  }

  /** The request the form describes. */
  request(): BulkApproveRequest {
    const { threshold, includeDerived, senderOnly } = this.form.getRawValue();
    const request: BulkApproveRequest = { threshold, includeDerived };
    if (senderOnly && this.data.senderAddress) request.senderAddress = this.data.senderAddress;
    return request;
  }

  submit(): void {
    if (this.sending()) return;
    this.sending.set(true);
    this.review
      .bulkApprove(this.request())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.sending.set(false);
          this.result.set(result);
        },
        // The error interceptor shows the server's problem detail.
        error: () => this.sending.set(false),
      });
  }
}

/** Opens the dialog; emits `true` when something may have been approved. */
export function openBulkApprove(
  dialog: MatDialog,
  data: BulkApproveDialogData,
): Observable<boolean> {
  const ref = dialog.open<BulkApproveDialog, BulkApproveDialogData, boolean>(BulkApproveDialog, {
    data,
    width: '32rem',
  });
  // Escape after a result still closed a dialog that approved.
  const instance = ref.componentInstance;
  return ref.afterClosed().pipe(map((closed) => closed === true || instance.result() !== null));
}
