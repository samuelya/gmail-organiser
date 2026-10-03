import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { DeleteConfirmData } from './clean-up.models';
import { DeleteConfirmResult, openDeleteConfirm } from './delete-confirm-dialog.component';

describe('DeleteConfirmDialog', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function open(data: Partial<DeleteConfirmData> = {}) {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    const results: (DeleteConfirmResult | undefined)[] = [];
    openDeleteConfirm(TestBed.inject(MatDialog), {
      labelName: 'Bin',
      scope: 'every sender',
      count: 10,
      protectedCount: 3,
      ...data,
    }).subscribe((r) => results.push(r));
    TestBed.tick();
    const q = <T extends HTMLElement>(id: string) =>
      document.querySelector<T>(`mat-dialog-container [data-testid="${id}"]`);
    return { results, q };
  }

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
  };

  it('skips protected messages by default and confirms with "Move to Trash"', async () => {
    const { results, q } = open();
    await settle();
    expect(q('delete-count')!.textContent).toContain('7 messages');
    expect(q('delete-count')!.textContent).toContain('Bin');
    expect(q('delete-protected')!.textContent).toContain('3 protected messages will be skipped');
    expect(q<HTMLInputElement>('include-protected')!.querySelector('input')!.checked).toBe(false);
    expect(q('delete-ok')!.textContent!.trim()).toBe('Move to Trash');
    q('delete-ok')!.click();
    await settle();
    expect(results).toEqual([{ includeProtected: false }]);
  });

  it('includes protected messages when ticked', async () => {
    const { results, q } = open();
    q('include-protected')!.querySelector('input')!.click();
    await settle();
    expect(q('delete-count')!.textContent).toContain('10 messages');
    expect(q('delete-protected')!.textContent).toContain('This includes 3 protected messages');
    q('delete-ok')!.click();
    await settle();
    expect(results).toEqual([{ includeProtected: true }]);
  });

  it('cancels with undefined and hides the checkbox when nothing is protected', async () => {
    const { results, q } = open({ protectedCount: 0 });
    expect(q('include-protected')).toBeNull();
    q('delete-cancel')!.click();
    await settle();
    expect(results).toEqual([undefined]);
  });

  it('disables "Move to Trash" when every message is protected', () => {
    const { q } = open({ count: 2, protectedCount: 2 });
    expect(q<HTMLButtonElement>('delete-ok')!.disabled).toBe(true);
  });
});
