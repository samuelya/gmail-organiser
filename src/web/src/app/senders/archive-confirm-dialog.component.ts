import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { map, Observable } from 'rxjs';
import { plural } from '../clean-up/clean-up.models';

export interface ArchiveConfirmData {
  senders: number;
  /** All the senders' messages; only the unprotected ones in the inbox are archived. */
  messages: number;
}

/** Confirms "Archive all" for the selected senders, with their message count. */
@Component({
  selector: 'app-archive-confirm-dialog',
  imports: [MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Archive all?</h2>
    <mat-dialog-content>
      <p class="m-0" data-testid="archive-count">
        The inbox mail among {{ plural(data.messages, 'message') }} from
        {{ plural(data.senders, 'sender') }} will be archived. Protected mail stays in the inbox.
        Undo from History.
      </p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false" data-testid="archive-cancel">
        Cancel
      </button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="archive-ok">
        Archive
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ArchiveConfirmDialog {
  readonly data = inject<ArchiveConfirmData>(MAT_DIALOG_DATA);
  readonly plural = plural;
}

/** Opens the dialog; emits `true` only when the user confirmed. Focus starts on Cancel. */
export function openArchiveConfirm(
  dialog: MatDialog,
  data: ArchiveConfirmData,
): Observable<boolean> {
  return dialog
    .open<ArchiveConfirmDialog, ArchiveConfirmData, boolean>(ArchiveConfirmDialog, {
      data,
      autoFocus: '[data-testid="archive-cancel"]',
    })
    .afterClosed()
    .pipe(map((confirmed) => confirmed === true));
}
