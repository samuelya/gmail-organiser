import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { Observable } from 'rxjs';
import { DeleteConfirmData, plural, trashCount } from './clean-up.models';

/** The confirmed Delete: whether protected messages go to Trash too. */
export interface DeleteConfirmResult {
  includeProtected: boolean;
}

/** Confirms a Delete: the count, the protected messages it skips and the opt-in to include them. */
@Component({
  selector: 'app-delete-confirm-dialog',
  imports: [MatButtonModule, MatCheckboxModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Move to Trash?</h2>
    <mat-dialog-content class="flex flex-col gap-3">
      <p class="m-0" data-testid="delete-count">
        {{ plural(count(), 'message') }} from {{ data.scope }} labelled “{{ data.labelName }}” will
        be moved to Trash. Gmail keeps Trash for 30 days; undo from History.
      </p>
      @if (data.protectedCount > 0) {
        <p class="m-0" data-testid="delete-protected">
          @if (includeProtected()) {
            This includes {{ plural(data.protectedCount, 'protected message') }}.
          } @else {
            {{ plural(data.protectedCount, 'protected message') }} will be skipped.
          }
        </p>
        <mat-checkbox
          [checked]="includeProtected()"
          (change)="includeProtected.set($event.checked)"
          data-testid="include-protected"
        >
          Include protected messages
        </mat-checkbox>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="delete-cancel">Cancel</button>
      <button
        mat-flat-button
        type="button"
        [disabled]="count() === 0"
        [mat-dialog-close]="{ includeProtected: includeProtected() }"
        data-testid="delete-ok"
      >
        Move to Trash
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeleteConfirmDialog {
  readonly data = inject<DeleteConfirmData>(MAT_DIALOG_DATA);
  readonly includeProtected = signal(false);
  readonly count = computed(() => trashCount(this.data, this.includeProtected()));
  readonly plural = plural;
}

/** Opens the dialog; emits the choice on "Move to Trash", `undefined` on cancel. Focus starts on Cancel. */
export function openDeleteConfirm(
  dialog: MatDialog,
  data: DeleteConfirmData,
): Observable<DeleteConfirmResult | undefined> {
  return dialog
    .open<DeleteConfirmDialog, DeleteConfirmData, DeleteConfirmResult>(DeleteConfirmDialog, {
      data,
      autoFocus: '[data-testid="delete-cancel"]',
    })
    .afterClosed();
}
