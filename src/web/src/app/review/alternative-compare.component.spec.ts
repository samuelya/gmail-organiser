import { TestBed } from '@angular/core/testing';
import { MatTooltip } from '@angular/material/tooltip';
import { AlternativeCompare } from './alternative-compare.component';
import { CompareValues, SuggestionAlternativeDto } from './alternative.models';

const current: CompareValues = {
  topicLabel: 'Topic/Alpha',
  documentTypeLabel: 'Type/Receipt',
  labelChange: 'add',
  replaceLabels: [],
  needsAction: false,
  toBeDeleted: true,
  confidenceMin: 0.7,
  confidenceMax: 0.9,
};

const alternative = (over: Partial<SuggestionAlternativeDto> = {}): SuggestionAlternativeDto => ({
  topicLabel: 'Topic/Beta',
  documentTypeLabel: 'Type/Receipt',
  replaceLabels: [],
  labelChange: 'add',
  needsAction: true,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.8,
  reason: 'Synthetic new reason',
  promptVersion: 'v9',
  model: 'model-a',
  createdAt: '2026-01-02T00:00:00Z',
  mixed: false,
  count: 1,
  ...over,
});

describe('AlternativeCompare', () => {
  async function render(alt = alternative(), now: CompareValues = current) {
    const fixture = TestBed.createComponent(AlternativeCompare);
    fixture.componentRef.setInput('current', now);
    fixture.componentRef.setInput('alternative', alt);
    fixture.componentRef.setInput('labels', { action: 'Act', delete: 'Bin' });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  it('shows current and new side by side with the differing new values highlighted', async () => {
    const { q } = await render();
    const now = q('alternative-current')!.textContent!;
    expect(now).toContain('Topic/Alpha');
    expect(now).toContain('Type/Receipt');
    expect(now).toContain('Bin');
    expect(now).toContain('Adds Topic/Alpha');
    expect(now).toContain('70%–90%');
    expect(q('alternative-source')!.textContent).toContain('Prompt v9 · model-a');
    expect(q('alt-topic')!.classList).toContain('diff');
    expect(q('alt-topic')!.textContent).toContain('(changed)');
    expect(q('alt-document-type')!.classList).not.toContain('diff');
    expect(q('alt-action')!.classList).toContain('diff');
    expect(q('alt-delete')!.textContent).toContain('No Bin');
    expect(q('alt-label-change')!.textContent).toContain('Adds Topic/Beta');
    expect(q('alt-label-change')!.classList).toContain('diff-chip');
    expect(q('alt-confidence')!.classList).not.toContain('diff-text');
    expect(q('alternative-reason')!.textContent).toContain('Synthetic new reason');
  });

  it('emits Use new and Keep current', async () => {
    const { fixture, q } = await render();
    const used = vi.fn();
    const kept = vi.fn();
    fixture.componentInstance.useNew.subscribe(used);
    fixture.componentInstance.keepCurrent.subscribe(kept);
    q('alternative-use')!.click();
    q('alternative-keep')!.click();
    expect(used).toHaveBeenCalledTimes(1);
    expect(kept).toHaveBeenCalledTimes(1);
    expect(q('alternative-use')!.textContent).toContain('Use new');
  });

  it('a mixed group shows the count and points to its members', async () => {
    const { fixture, q } = await render(alternative({ mixed: true, count: 3 }));
    const members = vi.fn();
    fixture.componentInstance.showMembers.subscribe(members);
    expect(q('alternative-current')).toBeNull();
    expect(q('alternative-mixed')!.textContent).toContain('3 members have a new result');
    expect(q('alternative-use')!.textContent).toContain('Use new (3)');
    q('alternative-members')!.click();
    expect(members).toHaveBeenCalled();
  });

  it('shows the comment as text on one line and opens it, line breaks kept, on click', async () => {
    const long = `<b>Synthetic</b> comment\nsecond line ${'word '.repeat(60)}end`;
    const { fixture, q } = await render(alternative({ comment: long }));
    const line = q('alternative-comment')!;
    expect(line.tagName).toBe('BUTTON');
    expect(line.textContent).toContain('Your comment: <b>Synthetic</b> comment');
    expect(line.querySelector('b')).toBeNull();
    expect(line.getAttribute('aria-expanded')).toBe('false');
    expect(getComputedStyle(line).whiteSpace).toBe('nowrap');
    expect(getComputedStyle(line).overflow).toBe('hidden');
    expect(getComputedStyle(line).textOverflow).toBe('ellipsis');
    line.click();
    await fixture.whenStable();
    expect(line.getAttribute('aria-expanded')).toBe('true');
    expect(getComputedStyle(line).whiteSpace).toBe('pre-line');
  });

  it('shows a Copied chip with its tooltip when copied', async () => {
    const { fixture, q } = await render(alternative({ copied: true }));
    expect(q('alternative-copied')!.textContent).toContain('Copied');
    const tip = fixture.debugElement
      .query((d) => d.attributes['data-testid'] === 'alternative-copied')
      .injector.get(MatTooltip);
    expect(tip.message).toBe('Copied from an email you accepted; no LLM');
    expect(q('alternative-copied')!.hasAttribute('tabindex')).toBe(false);
  });

  it('a mixed group shows neither the comment nor the chip', async () => {
    const { q } = await render(alternative({ copied: true, comment: 'x', mixed: true, count: 2 }));
    expect(q('alternative-mixed')).not.toBeNull();
    expect(q('alternative-comment')).toBeNull();
    expect(q('alternative-copied')).toBeNull();
  });

  it('shows neither the comment nor the chip when neither is set', async () => {
    const { q } = await render(alternative({ comment: null, copied: false }));
    expect(q('alternative-comment')).toBeNull();
    expect(q('alternative-copied')).toBeNull();
    expect(q('alternative-reason')).not.toBeNull();
  });

  it("shows the current extras and, on New, the union with the alternative's highlighted", async () => {
    const { el, q } = await render(
      alternative({ extraLabels: ['Archive', 'Kept'], extraLabelsNew: ['Archive'] }),
      { ...current, extraLabels: ['Kept'] },
    );
    const now = [...el.querySelectorAll('[data-testid="current-extra-label"]')];
    expect(now.map((e) => e.textContent!.trim())).toEqual(['Kept']);
    const next = [...el.querySelectorAll<HTMLElement>('[data-testid="alt-extra-label"]')];
    expect(next.map((e) => e.firstChild!.textContent!.trim())).toEqual(['Archive', 'Kept']);
    expect(next[0].classList).toContain('diff');
    expect(next[0].textContent).toContain('(added)');
    expect(q('alt-extra-label-new')!.textContent).toBe('new');
    expect(next[1].classList).not.toContain('diff');
    expect(next[1].textContent).not.toContain('new');
  });

  it('shows no extras when neither side has any', async () => {
    const { q } = await render();
    expect(q('current-extra-label')).toBeNull();
    expect(q('alt-extra-label')).toBeNull();
  });
});
