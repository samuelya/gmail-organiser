import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import {
  checkComment,
  MAX_REANALYSE_COMMENT,
  ReanalyseDialog,
  ReanalyseDialogData,
} from './reanalyse-dialog.component';

describe('ReanalyseDialog', () => {
  const data: ReanalyseDialogData = {
    title: 'Re-analyse 2 emails?',
    message: '2 emails go to the LLM.',
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
    const { el, q, type } = await render();
    expect(el.textContent).toContain('Re-analyse 2 emails?');
    expect(el.textContent).toContain('2 emails go to the LLM.');
    expect(el.textContent).toContain('Comment for the AI (optional)');
    expect(el.textContent).toContain(
      'For example which label fits better, or what these emails are',
    );
    expect(q('reanalyse-comment-count').textContent!.trim()).toBe('0 / 500');
    await type('abc');
    expect(q('reanalyse-comment-count').textContent!.trim()).toBe('3 / 500');
  });

  it('counts code points like the API: an emoji is one character, surrounding blanks none', async () => {
    const { q, type } = await render();
    await type(' \u{1F600}a\r\nb ');
    expect(q('reanalyse-comment-count').textContent!.trim()).toBe('4 / 500');
    expect(checkComment('\u{1F600}'.repeat(MAX_REANALYSE_COMMENT))).toBeNull();
    expect(checkComment('\u{1F600}'.repeat(MAX_REANALYSE_COMMENT + 1))).toEqual({ tooLong: true });
    expect(checkComment(` ${'a'.repeat(MAX_REANALYSE_COMMENT)} `)).toBeNull();
  });

  it('rejects what the API rejects and keeps ZWNJ, ZWJ, soft hyphen, newline and tab', () => {
    for (const bad of ['\u200B', '\uFEFF', '\u202E', '\u2060', '\u034F', '\u0007', '\uD800']) {
      expect(checkComment(`a${bad}b`)).toEqual({ invalidCharacter: true });
    }
    expect(checkComment('a\u200Cb\u200Dc\u00ADd\ne\tf')).toBeNull();
    expect(checkComment('\u{1F469}\u200D\u{1F4BB}')).toBeNull();
  });

  it('an invalid comment shows the reason and neither Enter nor Re-analyse closes the dialog', async () => {
    const { el, q, type, key, close } = await render();
    expect(q('reanalyse-comment-error')).toBeNull();
    await type('Pasted\u200Bsubject');
    expect(q('reanalyse-comment-error').textContent).toContain(
      'No control, format or invisible characters',
    );
    expect(q<HTMLButtonElement>('confirm-ok').disabled).toBe(true);
    expect(key({}).defaultPrevented).toBe(true);
    q('confirm-ok').click();
    expect(close).not.toHaveBeenCalled();

    await type('x'.repeat(MAX_REANALYSE_COMMENT + 1));
    expect(el.textContent).toContain(`At most ${MAX_REANALYSE_COMMENT} characters.`);
    await type('Pasted subject');
    expect(q('reanalyse-comment-error')).toBeNull();
    q('confirm-ok').click();
    expect(close).toHaveBeenLastCalledWith({ comment: 'Pasted subject' });
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
    expect(key({ keyCode: 229 }).defaultPrevented).toBe(false);
    expect(close).not.toHaveBeenCalled();
  });
});
