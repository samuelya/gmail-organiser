import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatTooltip } from '@angular/material/tooltip';
import { By } from '@angular/platform-browser';
import { RUN_ACTIVE_TOOLTIP } from '../analyse/compare-run';
import { ApplySimilarDialog, applySimilarSummary } from './apply-similar-dialog.component';
import { similar } from './review-page.testing';

describe('ApplySimilarDialog', () => {
  async function render(comment = 'These are receipts', blocked = false) {
    const close = vi.fn();
    const running = signal(blocked);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            similar: similar({ count: 3, suggestionIds: ['b', 'c', 'd'] }),
            comment,
            blocked: running,
          },
        },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(ApplySimilarDialog);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends HTMLElement>(id: string) => el.querySelector<T>(`[data-testid="${id}"]`);
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    const tooltip = () =>
      fixture.debugElement.query(By.css('[data-testid="similar-ok"]')).injector.get(MatTooltip)
        .message;
    return { fixture, el, q, settle, close, running, tooltip };
  }

  it('summarises the count, the results it replaces and the cap', () => {
    expect(applySimilarSummary(similar({ count: 1, suggestionIds: ['b'] }))).toEqual([
      '1 pending email from this sender.',
    ]);
    expect(
      applySimilarSummary(
        similar({ count: 600, suggestionIds: ['b', 'c'], withAlternative: 2, truncated: true }),
      ),
    ).toEqual([
      '600 pending emails from this sender.',
      'Only the newest 2 are taken.',
      '2 already have a re-analysis result that will be replaced.',
    ]);
  });

  it('defaults to re-analyse with the accepted comment and closes with the trimmed comment', async () => {
    const { q, settle, close } = await render();
    expect(q('similar-summary')!.textContent).toContain('3 pending emails from this sender.');
    const reanalyse = q('similar-mode-reanalyse')!.querySelector('input')!;
    expect(reanalyse.checked).toBe(true);
    const textarea = q<HTMLTextAreaElement>('similar-comment')!;
    expect(textarea.value).toBe('These are receipts');
    expect(q('similar-ok')!.textContent).toContain('Re-analyse');
    textarea.value = '  Receipts, not newsletters \r\n';
    textarea.dispatchEvent(new Event('input'));
    await settle();
    q('similar-ok')!.click();
    expect(close).toHaveBeenCalledWith({ mode: 'reanalyse', comment: 'Receipts, not newsletters' });
  });

  it('leaves the comment out when it is cleared, and blocks one the API would reject', async () => {
    const { q, settle, close } = await render();
    const textarea = q<HTMLTextAreaElement>('similar-comment')!;
    textarea.value = 'x'.repeat(501);
    textarea.dispatchEvent(new Event('input'));
    await settle();
    expect(q<HTMLButtonElement>('similar-ok')!.getAttribute('aria-disabled')).toBe('true');
    q('similar-ok')!.click();
    expect(close).not.toHaveBeenCalled();
    textarea.value = '   ';
    textarea.dispatchEvent(new Event('input'));
    await settle();
    q('similar-ok')!.click();
    expect(close).toHaveBeenCalledWith({ mode: 'reanalyse' });
  });

  it('copies without the AI, hiding the comment', async () => {
    const { q, settle, close } = await render('');
    q('similar-mode-copy')!.querySelector('input')!.click();
    await settle();
    expect(q('similar-comment')).toBeNull();
    expect(q('similar-ok')!.textContent).toContain('Copy');
    q('similar-ok')!.click();
    expect(close).toHaveBeenCalledWith({ mode: 'copy' });
  });

  it('blocks both modes while an analysis run is active', async () => {
    const { q, settle, close, running, tooltip } = await render('', true);
    await settle();
    expect(q('similar-ok')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip()).toBe(RUN_ACTIVE_TOOLTIP);
    q('similar-mode-copy')!.querySelector('input')!.click();
    await settle();
    q('similar-ok')!.click();
    expect(close).not.toHaveBeenCalled();
    running.set(false);
    await settle();
    expect(tooltip()).toBe('');
    q('similar-ok')!.click();
    expect(close).toHaveBeenCalledWith({ mode: 'copy' });
  });
});
