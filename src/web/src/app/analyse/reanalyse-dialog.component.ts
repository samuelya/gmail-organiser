import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export interface ReanalyseDialogData {
  title: string;
  message: string;
  /** Most characters the comment takes. */
  maxComment: number;
}

/** The confirmed re-analysis: the comment for the AI, only when one was typed. */
export interface ReanalyseDialogResult {
  comment?: string;
}

/**
 * Confirms a re-analysis with an optional comment for the AI. Closes with a `ReanalyseDialogResult` on
 * confirm (also by Enter in the comment; Shift+Enter is a line break), anything else is a cancel.
 */
@Component({
  selector: 'app-reanalyse-dialog',
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    ReactiveFormsModule,
  ],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content class="flex flex-col gap-3">
      <p class="m-0">{{ data.message }}</p>
      <mat-form-field class="w-full" subscriptSizing="dynamic">
        <mat-label>Comment for the AI (optional)</mat-label>
        <textarea
          matInput
          cdkFocusInitial
          rows="3"
          [maxlength]="data.maxComment"
          [formControl]="comment"
          (keydown)="onKeydown($event)"
          data-testid="reanalyse-comment"
        ></textarea>
        <mat-hint>For example which label fits better, or what these emails are</mat-hint>
        <mat-hint align="end" data-testid="reanalyse-comment-count">
          {{ comment.value.length }} / {{ data.maxComment }}
        </mat-hint>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="confirm-cancel">Cancel</button>
      <button mat-flat-button type="button" (click)="confirm()" data-testid="confirm-ok">
        Re-analyse
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReanalyseDialog {
  readonly data = inject<ReanalyseDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ReanalyseDialog, ReanalyseDialogResult>>(MatDialogRef);
  readonly comment = new FormControl('', { nonNullable: true });

  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return;
    event.preventDefault();
    this.confirm();
  }

  confirm(): void {
    const comment = this.comment.value.trim();
    this.ref.close(comment ? { comment } : {});
  }
}
