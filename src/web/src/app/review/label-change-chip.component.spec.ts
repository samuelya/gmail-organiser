import { TestBed } from '@angular/core/testing';
import { LabelChangeChip } from './label-change-chip.component';
import { changedReplaceLabels, LabelChange, labelChangeText } from './review.models';

const change = (labelChange: LabelChange, replaceLabels: string[] = []) => ({
  labelChange,
  topicLabel: 'Projects/Alpha',
  replaceLabels,
});

describe('labelChangeText', () => {
  it('describes each label change and nothing for none', () => {
    expect(labelChangeText(change('keep'))).toBe('Keeps Projects/Alpha');
    expect(labelChangeText(change('add'))).toBe('Adds Projects/Alpha');
    expect(labelChangeText(change('move', ['Inbox/Old']))).toBe('Moves Inbox/Old → Projects/Alpha');
    expect(labelChangeText(change('relabel', ['A', 'B/C']))).toBe(
      'Relabels A, B/C → Projects/Alpha',
    );
    expect(labelChangeText(change('none'))).toBeNull();
  });
});

describe('changedReplaceLabels', () => {
  it('is undefined for the same set in any order, else the checked labels', () => {
    expect(changedReplaceLabels(['A', 'B'], ['B', 'A'])).toBeUndefined();
    expect(changedReplaceLabels(['A'], [])).toEqual([]);
    expect(changedReplaceLabels([], ['A'])).toEqual(['A']);
  });
});

describe('LabelChangeChip', () => {
  function render(value: ReturnType<typeof change>) {
    const fixture = TestBed.createComponent(LabelChangeChip);
    fixture.componentRef.setInput('change', value);
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="label-change"]',
    );
  }

  it('renders the text with a title carrying it in full', () => {
    const chip = render(change('relabel', ['A', 'B']))!;
    expect(chip.textContent!.trim()).toBe('Relabels A, B → Projects/Alpha');
    expect(chip.getAttribute('title')).toBe('Relabels A, B → Projects/Alpha');
  });

  it('renders nothing for none', () => {
    expect(render(change('none'))).toBeNull();
  });
});
