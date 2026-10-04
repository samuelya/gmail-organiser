import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { openPurge } from './purge-dialog.component';

/**
 * The Settings page's "Data" section (#202): "Purge local data" behind a typed confirmation. A
 * purge ends on the Dashboard; open pages reload from the `dataPurged` event.
 */
@Component({
  selector: 'app-data-settings',
  imports: [MatButtonModule],
  template: `
    <button
      mat-stroked-button
      type="button"
      class="purge-open"
      (click)="open()"
      data-testid="purge-open"
    >
      Purge local data
    </button>
  `,
  styles: `
    .purge-open {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataSettingsSection {
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  open(): void {
    openPurge(this.dialog)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((response) => {
        if (!response) return;
        this.snackBar.open('Local data purged', undefined, { duration: 3000 });
        void this.router.navigateByUrl('/dashboard');
      });
  }
}
