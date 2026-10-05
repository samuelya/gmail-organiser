import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { map, Observable } from 'rxjs';
import { ApproveSummary } from './policies.models';

export interface ApprovePolicyDialogData {
  /** The sender, domain or list the policy is for. */
  name: string;
  summary: ApproveSummary;
}

/** Confirms an approve: what the policy applies to now, and what it leaves. Focus starts on Cancel. */
@Component({
  selector: 'app-approve-policy-dialog',
  imports: [DecimalPipe, MatButtonModule, MatDialogModule],
  template: `
    @let s = data.summary;
    <h2 mat-dialog-title>Approve policy?</h2>
    <mat-dialog-content class="flex flex-col gap-2">
      <p class="m-0" data-testid="approve-matched">
        It applies to {{ s.matched | number }} of {{ s.sampledMessages | number }}
        {{ s.sampled ? 'previewed' : '' }} messages from {{ data.name }} now, and to new mail as it
        arrives. Undo from History.
      </p>
      @if (s.sampled) {
        <p class="muted m-0 text-sm" data-testid="approve-sampled">
          Counted on the newest {{ s.sampledMessages | number }} messages; the apply job covers all
          of them.
        </p>
      }
      @if (s.guarded) {
        <p class="m-0" data-testid="approve-guarded">
          {{ s.guarded | number }} transactional
          {{ s.guarded === 1 ? 'message is' : 'messages are' }}
          kept from deletion.
        </p>
      }
      @if (s.mixed) {
        <p class="m-0" data-testid="approve-mixed">
          Mixed: unmatched mail stays for analysis ({{ s.unmatched | number }} now).
        </p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false" data-testid="approve-cancel">
        Cancel
      </button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="approve-ok">
        Approve and apply
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApprovePolicyDialog {
  readonly data = inject<ApprovePolicyDialogData>(MAT_DIALOG_DATA);
}

/** Emits `true` only when the user confirmed. */
export function openApprovePolicy(
  dialog: MatDialog,
  data: ApprovePolicyDialogData,
): Observable<boolean> {
  return dialog
    .open<ApprovePolicyDialog, ApprovePolicyDialogData, boolean>(ApprovePolicyDialog, {
      data,
      autoFocus: '[data-testid="approve-cancel"]',
    })
    .afterClosed()
    .pipe(map((result) => result === true));
}
