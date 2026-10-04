import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { map, Observable } from 'rxjs';
import { KIND_TITLES, KindCounts, PLAN_KINDS } from './label-plan.models';

/** "Apply accepted": the accepted items per kind and what applying them does; closes with `true` on Apply. */
@Component({
  selector: 'app-apply-plan-dialog',
  imports: [DecimalPipe, MatButtonModule, MatDialogModule],
  template: `
    <h2 mat-dialog-title>Apply the accepted label changes?</h2>
    <mat-dialog-content>
      <ul class="m-0 pl-5" data-testid="apply-counts">
        @for (kind of kinds; track kind) {
          @if (counts[kind]) {
            <li>{{ titles[kind] }}: {{ counts[kind] | number }}</li>
          }
        }
      </ul>
      <p class="mb-0">Merges are undoable from History; source labels are kept.</p>
      <p class="mb-0">Empty labels are deleted only when still empty.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false" data-testid="apply-cancel">
        Cancel
      </button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="apply-ok">
        Apply
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplyPlanDialog {
  readonly counts = inject<KindCounts>(MAT_DIALOG_DATA);
  readonly kinds = PLAN_KINDS;
  readonly titles = KIND_TITLES;
}

/** Opens the dialog; emits `true` only when the user confirmed. */
export function openApplyPlan(dialog: MatDialog, counts: KindCounts): Observable<boolean> {
  return dialog
    .open<ApplyPlanDialog, KindCounts, boolean>(ApplyPlanDialog, { data: counts })
    .afterClosed()
    .pipe(map((result) => result === true));
}
