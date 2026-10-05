import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSliderModule } from '@angular/material/slider';
import { map, Observable } from 'rxjs';
import { ANALYSIS_LIMITS } from '../settings/settings.models';
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
  /** Bulk approve skips suggestions that propose a label Gmail lacks. */
  taxonomyLocked: boolean;
}

/** The dialog's data: the threshold limits and lock from settings, and the selected sender. */
export function bulkApproveData(
  settings: { bulkApproveThreshold: number; taxonomyLocked?: boolean } | null,
  senderAddress: string | null,
): BulkApproveDialogData {
  const limits = ANALYSIS_LIMITS.bulkApproveThreshold;
  return {
    threshold: settings?.bulkApproveThreshold ?? limits.max,
    min: limits.min,
    max: limits.max,
    step: limits.step,
    senderAddress,
    taxonomyLocked: settings?.taxonomyLocked ?? false,
  };
}

/** The exclusion sentence before approving, when the count is not known yet. */
export const NEW_LABEL_EXCLUDED =
  'Suggestions with new labels are excluded while the taxonomy is locked.';

/** "n suggestions with new labels were excluded while the taxonomy is locked"; null for none. */
export function newLabelExclusionText(count: number): string | null {
  if (count <= 0) return null;
  const subject =
    count === 1 ? 'suggestion with a new label was' : 'suggestions with new labels were';
  return `${count} ${subject} excluded while the taxonomy is locked.`;
}

/**
 * The exclusion sentence: the API's `excludedNewLabel` once approved, else the generic sentence while the
 * taxonomy is locked. Null when none applies.
 */
export function exclusionFor(
  data: BulkApproveDialogData,
  result: BulkApproveResponse | null,
): string | null {
  if (result) return newLabelExclusionText(result.excludedNewLabel);
  return data.taxonomyLocked ? NEW_LABEL_EXCLUDED : null;
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
          <mat-slider class="mt-4" [min]="data.min" [max]="data.max" [step]="data.step" discrete>
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
      @if (exclusion(); as text) {
        <p class="m-0 mt-3" data-testid="bulk-new-label-excluded">
          {{ text }} Approve them individually.
        </p>
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
  private readonly value = toSignal(
    this.form.valueChanges.pipe(map(() => this.form.getRawValue())),
    {
      initialValue: this.form.getRawValue(),
    },
  );
  readonly exclusion = computed(() => exclusionFor(this.data, this.result()));

  thresholdText(): string {
    return percent(this.value().threshold);
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
