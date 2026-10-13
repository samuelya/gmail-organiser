import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { ShowOnDirtyErrorStateMatcher } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

/** The API's `AnalysisRunService.MaxCommentLength`: longest comment for the AI, in code points. */
export const MAX_REANALYSE_COMMENT = 500;

/** Kept although the API rejects other format characters (ZWNJ, ZWJ, soft hyphen), like the API. */
const ALLOWED = new Set([0x0a, 0x09, 0x200c, 0x200d, 0x00ad]);
/** Control, format and lone surrogates, plus the combining grapheme joiner the API counts as invisible. */
const REJECTED = /^[\p{Cc}\p{Cf}\p{Cs}\u034F]$/u;

/** The comment as the API stores it: line breaks as `\n`, trimmed. */
export function normaliseComment(value: string): string {
  return value.replace(/\r\n?/g, '\n').trim();
}

/** Length in code points, as the API counts it. */
export function commentLength(value: string): number {
  return [...normaliseComment(value)].length;
}

/** Mirrors the API's `AnalysisRunService.CheckComment`, so a comment it would reject never leaves the dialog. */
export function checkComment(value: string): ValidationErrors | null {
  const chars = [...normaliseComment(value)];
  if (chars.length > MAX_REANALYSE_COMMENT) return { tooLong: true };
  return chars.some((c) => !ALLOWED.has(c.codePointAt(0)!) && REJECTED.test(c))
    ? { invalidCharacter: true }
    : null;
}

function commentValidator(control: AbstractControl<string>): ValidationErrors | null {
  return checkComment(control.value ?? '');
}

export interface ReanalyseDialogData {
  title: string;
  message: string;
}

/** The confirmed re-analysis: the comment for the AI, only when one was typed. */
export interface ReanalyseDialogResult {
  comment?: string;
}

/**
 * Confirms a re-analysis with an optional comment for the AI. Closes with a `ReanalyseDialogResult` on
 * confirm (also by Enter in the comment; Shift+Enter is a line break), anything else is a cancel. A comment the
 * API would reject shows the reason and keeps the dialog open.
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
          [formControl]="comment"
          [errorStateMatcher]="errorState"
          (keydown)="onKeydown($event)"
          data-testid="reanalyse-comment"
        ></textarea>
        <mat-hint>For example which label fits better, or what these emails are</mat-hint>
        <mat-hint align="end" data-testid="reanalyse-comment-count">
          {{ length() }} / {{ max }}
        </mat-hint>
        @if (comment.hasError('tooLong')) {
          <mat-error data-testid="reanalyse-comment-error">At most {{ max }} characters.</mat-error>
        } @else if (comment.hasError('invalidCharacter')) {
          <mat-error data-testid="reanalyse-comment-error">
            No control, format or invisible characters other than newline and tab.
          </mat-error>
        }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="confirm-cancel">Cancel</button>
      <button
        mat-flat-button
        type="button"
        [disabled]="!valid()"
        (click)="confirm()"
        data-testid="confirm-ok"
      >
        Re-analyse
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReanalyseDialog {
  readonly data = inject<ReanalyseDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<ReanalyseDialog, ReanalyseDialogResult>>(MatDialogRef);
  readonly max = MAX_REANALYSE_COMMENT;
  readonly errorState = new ShowOnDirtyErrorStateMatcher();
  readonly comment = new FormControl('', { nonNullable: true, validators: commentValidator });
  private readonly value = toSignal(this.comment.valueChanges, { initialValue: '' });
  readonly length = computed(() => commentLength(this.value()));
  readonly valid = computed(() => checkComment(this.value()) === null);

  onKeydown(event: KeyboardEvent): void {
    // keyCode 229: Safari's Enter that commits an IME composition arrives with isComposing false.
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229)
      return;
    event.preventDefault();
    this.confirm();
  }

  confirm(): void {
    if (this.comment.invalid) return;
    const comment = normaliseComment(this.comment.value);
    this.ref.close(comment ? { comment } : {});
  }
}
