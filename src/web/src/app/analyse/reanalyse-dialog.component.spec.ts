import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ReanalyseDialog, ReanalyseDialogData } from './reanalyse-dialog.component';

describe('ReanalyseDialog', () => {
  const data: ReanalyseDialogData = {
    title: 'Re-analyse 2 emails?',
    message: '2 emails go to the LLM.',
    maxComment: 20,
  };

  async function render() {
    const close = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(ReanalyseDialog);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends HTMLElement>(id: string) => el.querySelector<T>(`[data-testid="${id}"]`)!;
    const textarea = q<HTMLTextAreaElement>('reanalyse-comment');
    const type = async (value: string) => {
      textarea.value = value;
      textarea.dispatchEvent(new Event('input'));
      await fixture.whenStable();
    };
    const key = (init: KeyboardEventInit) => {
      const event = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, ...init });
      textarea.dispatchEvent(event);
      return event;
    };
    return { el, q, textarea, type, key, close };
  }

  it('shows the title, the text and the labelled comment field with hint and counter', async () => {
    const { el, q, textarea, type } = await render();
    expect(el.textContent).toContain('Re-analyse 2 emails?');
    expect(el.textContent).toContain('2 emails go to the LLM.');
    expect(el.textContent).toContain('Comment for the AI (optional)');
    expect(el.textContent).toContain(
      'For example which label fits better, or what these emails are',
    );
    expect(textarea.getAttribute('maxlength')).toBe('20');
    expect(q('reanalyse-comment-count').textContent!.trim()).toBe('0 / 20');
    await type('abc');
    expect(q('reanalyse-comment-count').textContent!.trim()).toBe('3 / 20');
  });

  it('Re-analyse closes with the trimmed comment, or none when it is blank', async () => {
    const { q, type, close } = await render();
    await type('  Invoices \n');
    q('confirm-ok').click();
    expect(close).toHaveBeenLastCalledWith({ comment: 'Invoices' });
    await type('  \n ');
    q('confirm-ok').click();
    expect(close).toHaveBeenLastCalledWith({});
  });

  it('Enter submits, also with an empty comment', async () => {
    const { type, key, close } = await render();
    const empty = key({});
    expect(empty.defaultPrevented).toBe(true);
    expect(close).toHaveBeenLastCalledWith({});
    await type('Receipts');
    key({});
    expect(close).toHaveBeenLastCalledWith({ comment: 'Receipts' });
  });

  it('Shift+Enter and Enter during an IME composition do not submit', async () => {
    const { type, key, close } = await render();
    await type('Line one');
    expect(key({ shiftKey: true }).defaultPrevented).toBe(false);
    expect(key({ isComposing: true }).defaultPrevented).toBe(false);
    expect(close).not.toHaveBeenCalled();
  });
});
