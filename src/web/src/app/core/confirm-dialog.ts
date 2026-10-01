import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { Observable, map } from 'rxjs';

export interface ConfirmDialogData {
  title: string;
  message: string;
  /** Label of the confirming button. */
  confirm: string;
}

/** A yes/no dialog: closes with `true` on confirm, anything else is a cancel. Focus starts on Cancel. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="m-0">{{ data.message }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false" data-testid="confirm-cancel">
        Cancel
      </button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="confirm-ok">
        {{ data.confirm }}
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
}

/** Opens the confirm dialog; emits `true` only when the user confirmed. */
export function openConfirm(dialog: MatDialog, data: ConfirmDialogData): Observable<boolean> {
  return dialog
    .open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, { data })
    .afterClosed()
    .pipe(map((result) => result === true));
}
