import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Observable } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { PURGE_CONFIRMATION_WORD, PurgeResponse } from './settings.models';
import { SettingsService } from './settings.service';

/** Shown for a 409 without a problem detail. */
export const PURGE_CONFLICT_FALLBACK = 'Wait for running jobs to finish or cancel them first.';

/**
 * "Purge local data": what is removed and kept, and a typed confirmation. Calls the API itself and
 * closes with the response; a 409 (jobs active, or a Gmail batch half-applied) keeps it open.
 */
@Component({
  selector: 'app-purge-dialog',
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    ReactiveFormsModule,
  ],
  template: `
    <h2 mat-dialog-title>Purge local data</h2>
    <mat-dialog-content>
      <p class="mt-0" data-testid="purge-copy">
        This removes everything the app fetched or produced: message metadata, senders, analysis
        runs, suggestions, decision memory, history and undo log, jobs and Claude review items. It
        keeps your Google connection, settings and models. Nothing changes in Gmail. Type
        <strong>{{ word }}</strong> to confirm.
      </p>
      <mat-form-field class="w-full" subscriptSizing="dynamic">
        <mat-label>Type {{ word }} to confirm</mat-label>
        <input
          matInput
          [formControl]="confirm"
          autocomplete="off"
          (keydown.enter)="run()"
          data-testid="purge-confirm"
        />
      </mat-form-field>
      @if (error(); as message) {
        <p class="purge-error mb-0" role="alert" data-testid="purge-error">{{ message }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [disabled]="purging()" mat-dialog-close>Cancel</button>
      <button
        mat-flat-button
        type="button"
        color="warn"
        class="purge-run"
        [disabled]="!confirmed() || purging()"
        (click)="run()"
        data-testid="purge-run"
      >
        Purge local data
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .purge-error {
      color: var(--mat-sys-error);
    }
    .purge-run:not([disabled]) {
      background-color: var(--mat-sys-error);
      color: var(--mat-sys-on-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurgeDialog {
  private readonly dialogRef = inject<MatDialogRef<PurgeDialog, PurgeResponse>>(MatDialogRef);
  private readonly settingsApi = inject(SettingsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly word = PURGE_CONFIRMATION_WORD;
  readonly confirm = new FormControl('', { nonNullable: true });
  private readonly value = toSignal(this.confirm.valueChanges, { initialValue: '' });
  readonly purging = signal(false);
  readonly error = signal<string | null>(null);

  confirmed(): boolean {
    return this.value().trim() === PURGE_CONFIRMATION_WORD;
  }

  run(): void {
    if (!this.confirmed() || this.purging()) return;
    this.purging.set(true);
    this.error.set(null);
    this.dialogRef.disableClose = true;
    this.settingsApi
      .purge(this.confirm.value.trim())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => this.dialogRef.close(response),
        // The error interceptor shows why; a 409 also stays in the dialog.
        error: (error: unknown) => {
          this.purging.set(false);
          this.dialogRef.disableClose = false;
          if (error instanceof HttpErrorResponse && error.status === 409) {
            this.error.set(conflictMessage(error));
          }
        },
      });
  }
}

function conflictMessage(error: HttpErrorResponse): string {
  const body = error.error as { title?: unknown; detail?: unknown } | null;
  return typeof body?.detail === 'string' || typeof body?.title === 'string'
    ? errorMessage(error)
    : PURGE_CONFLICT_FALLBACK;
}

/** Opens the dialog; emits the response when the purge ran, else `undefined`. */
export function openPurge(dialog: MatDialog): Observable<PurgeResponse | undefined> {
  return dialog
    .open<PurgeDialog, void, PurgeResponse>(PurgeDialog, {
      width: '32rem',
      autoFocus: 'first-tabbable',
    })
    .afterClosed();
}
