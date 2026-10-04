import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { DataSettingsSection } from './data-settings.component';
import { PurgeDialog } from './purge-dialog.component';
import { PurgeResponse } from './settings.models';

const purged: PurgeResponse = { tables: ['messages'], purgedAt: '2026-01-01T00:00:00Z' };

describe('DataSettingsSection', () => {
  const dialog = { open: vi.fn() };
  const snackBar = { open: vi.fn() };
  const router = { navigateByUrl: vi.fn(() => Promise.resolve(true)) };

  function render(result: PurgeResponse | undefined) {
    dialog.open.mockReset().mockReturnValue({ afterClosed: () => of(result) });
    snackBar.open.mockReset();
    router.navigateByUrl.mockClear();
    TestBed.configureTestingModule({
      providers: [
        { provide: MatDialog, useValue: dialog },
        { provide: MatSnackBar, useValue: snackBar },
        { provide: Router, useValue: router },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(DataSettingsSection);
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="purge-open"]')!
      .click();
  }

  it('after a purge shows a snackbar and goes to the Dashboard', () => {
    render(purged);
    expect(dialog.open).toHaveBeenCalledWith(PurgeDialog, expect.anything());
    expect(snackBar.open).toHaveBeenCalledWith('Local data purged', undefined, { duration: 3000 });
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('does nothing when the dialog is cancelled', () => {
    render(undefined);
    expect(snackBar.open).not.toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });
});
