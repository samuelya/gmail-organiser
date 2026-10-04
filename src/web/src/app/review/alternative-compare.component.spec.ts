import { TestBed } from '@angular/core/testing';
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
  async function render(alt = alternative()) {
    const fixture = TestBed.createComponent(AlternativeCompare);
    fixture.componentRef.setInput('current', current);
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
});
