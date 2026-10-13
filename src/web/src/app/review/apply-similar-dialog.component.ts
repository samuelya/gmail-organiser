import { ChangeDetectionStrategy, Component, computed, inject, Signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { ShowOnDirtyErrorStateMatcher } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RUN_ACTIVE_TOOLTIP } from '../analyse/compare-run';
import {
  checkComment,
  commentLength,
  MAX_REANALYSE_COMMENT,
  normaliseComment,
} from '../analyse/reanalyse-dialog.component';
import { SimilarPendingDto } from './review.models';

export interface ApplySimilarDialogData {
  similar: SimilarPendingDto;
  /** The accepted re-analysis result's comment, the textarea's starting value. */
  comment: string;
  /** An analysis run is queued or running: neither mode may start. */
  blocked: Signal<boolean>;
}

export type ApplySimilarMode = 'reanalyse' | 'copy';

/** Re-analyse with the comment (only when one was typed), or copy the accepted outcome. */
export type ApplySimilarResult = { mode: 'reanalyse'; comment?: string } | { mode: 'copy' };

/** What the dialog covers: the sender's pending emails, the results it replaces and the cap. */
export function applySimilarSummary(s: SimilarPendingDto): string[] {
  const noun = s.count === 1 ? 'pending email' : 'pending emails';
  const lines = [`${s.count.toLocaleString()} ${noun} from this sender.`];
  if (s.truncated)
    lines.push(`Only the newest ${s.suggestionIds.length.toLocaleString()} are taken.`);
  if (s.withAlternative > 0) {
    const have = s.withAlternative === 1 ? 'already has' : 'already have';
    lines.push(
      `${s.withAlternative.toLocaleString()} ${have} a re-analysis result that will be replaced.`,
    );
  }
  return lines;
}

/**
 * "Apply to similar pending emails" after a "Use new": re-analyse the sender's similar pending emails with the
 * accepted result's comment, or copy that outcome to them without the AI. Either way the results wait for "Use new"
 * or "Keep current"; nothing is approved or applied. Closes with an `ApplySimilarResult`, anything else is a cancel.
 */
@Component({
  selector: 'app-apply-similar-dialog',
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatRadioModule,
    MatTooltipModule,
    ReactiveFormsModule,
  ],
  template: `
    <h2 mat-dialog-title>Apply to similar pending emails</h2>
    <mat-dialog-content class="flex flex-col gap-3">
      <div data-testid="similar-summary">
        @for (line of summary; track $index) {
          <p class="m-0">{{ line }}</p>
        }
      </div>
      <mat-radio-group
        [formControl]="mode"
        aria-label="How to apply"
        class="flex flex-col gap-1"
        data-testid="similar-mode"
      >
        <mat-radio-button value="reanalyse" data-testid="similar-mode-reanalyse">
          Re-analyse them with this comment
        </mat-radio-button>
        <mat-radio-button value="copy" data-testid="similar-mode-copy">
          Copy this outcome for review (no AI)
        </mat-radio-button>
      </mat-radio-group>
      @if (modeValue() === 'reanalyse') {
        <mat-form-field class="w-full" subscriptSizing="dynamic">
          <mat-label>Comment for the AI (optional)</mat-label>
          <textarea
            matInput
            rows="3"
            [formControl]="comment"
            [errorStateMatcher]="errorState"
            data-testid="similar-comment"
          ></textarea>
          <mat-hint align="end" data-testid="similar-comment-count">
            {{ length() }} / {{ max }}
          </mat-hint>
          @if (comment.hasError('tooLong')) {
            <mat-error data-testid="similar-comment-error">At most {{ max }} characters.</mat-error>
          } @else if (comment.hasError('invalidCharacter')) {
            <mat-error data-testid="similar-comment-error">
              No control, format or invisible characters other than newline and tab.
            </mat-error>
          }
        </mat-form-field>
      }
      <p class="m-0">
        The results wait under Re-analysed for "Use new" or "Keep current". Nothing is approved or
        applied.
      </p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="similar-cancel">Cancel</button>
      <button
        mat-flat-button
        type="button"
        [disabled]="!canStart()"
        [disabledInteractive]="true"
        [matTooltip]="data.blocked() ? runActiveTooltip : ''"
        (click)="confirm()"
        data-testid="similar-ok"
      >
        {{ modeValue() === 'copy' ? 'Copy' : 'Re-analyse' }}
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplySimilarDialog {
  readonly data = inject<ApplySimilarDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ApplySimilarDialog, ApplySimilarResult>>(MatDialogRef);
  readonly max = MAX_REANALYSE_COMMENT;
  readonly runActiveTooltip = RUN_ACTIVE_TOOLTIP;
  readonly errorState = new ShowOnDirtyErrorStateMatcher();
  readonly summary = applySimilarSummary(this.data.similar);
  readonly mode = new FormControl<ApplySimilarMode>('reanalyse', { nonNullable: true });
  readonly comment = new FormControl(this.data.comment, {
    nonNullable: true,
    validators: (c) => checkComment(c.value ?? ''),
  });
  readonly modeValue = toSignal(this.mode.valueChanges, { initialValue: this.mode.value });
  private readonly commentValue = toSignal(this.comment.valueChanges, {
    initialValue: this.comment.value,
  });
  readonly length = computed(() => commentLength(this.commentValue()));
  readonly canStart = computed(
    () =>
      !this.data.blocked() &&
      (this.modeValue() === 'copy' || checkComment(this.commentValue()) === null),
  );

  confirm(): void {
    if (!this.canStart()) return;
    if (this.mode.value === 'copy') {
      this.ref.close({ mode: 'copy' });
      return;
    }
    const comment = normaliseComment(this.comment.value);
    this.ref.close(comment ? { mode: 'reanalyse', comment } : { mode: 'reanalyse' });
  }
}
