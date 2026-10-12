import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { filter, finalize, switchMap, tap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { ApplyTracker } from './apply-tracker';
import { applyAllConfirm, applyAllFailure } from './review.models';
import { ReviewService } from './review.service';

/**
 * Review header "Apply all approved (n)": confirm, then apply every sender's approved suggestions,
 * followed by the page's `ApplyTracker`. Hidden while `count` is 0.
 */
@Component({
  selector: 'app-review-apply-all',
  imports: [MatButtonModule],
  template: `
    @if (count(); as n) {
      <button
        mat-flat-button
        type="button"
        [disabled]="disabled() || posting() || !!apply.jobId()"
        (click)="applyAll()"
        data-testid="apply-all"
      >
        Apply all approved ({{ n }})
      </button>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplyAllButton {
  private readonly review = inject(ReviewService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  readonly apply = inject(ApplyTracker);

  /** Approved suggestions across every sender. */
  readonly count = input.required<number>();
  /** Another page action is running. */
  readonly disabled = input(false);
  /** The request ended, applied or not: the page re-fetches. */
  readonly changed = output();
  readonly posting = signal(false);

  applyAll(): void {
    const n = this.count();
    if (!n || this.posting() || this.apply.jobId()) return;
    openConfirm(this.dialog, applyAllConfirm(n))
      .pipe(
        filter(Boolean),
        tap(() => this.posting.set(true)),
        switchMap(() =>
          this.review.applyAll().pipe(
            finalize(() => {
              this.posting.set(false);
              this.changed.emit();
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (batch) => this.apply.track(batch.jobId),
        // Other errors were shown by the error interceptor.
        error: (e: unknown) => {
          const message = applyAllFailure(e);
          if (message) this.snackBar.open(message, 'Dismiss', { duration: 10_000 });
        },
      });
  }
}
