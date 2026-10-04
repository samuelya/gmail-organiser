import { TestBed } from '@angular/core/testing';
import { LabelChangeChip } from './label-change-chip.component';
import {
  decidedReplaceLabels,
  LabelChange,
  labelChangeText,
  replaceState,
  SuggestionDto,
} from './review.models';

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

  it('a group move names every source label', () => {
    expect(labelChangeText(change('move', ['Old/Sub', 'Sub']))).toBe(
      'Moves Old/Sub, Sub → Projects/Alpha',
    );
  });
});

const member = (currentLabels: string[], replaceLabels: string[]) =>
  ({ currentLabels, replaceLabels }) as SuggestionDto;

describe('replaceState', () => {
  it('is true when every carrier replaces the label, false when none does, null when some do', () => {
    const a = member(['X', 'Y'], ['X']);
    const b = member(['X', 'Y'], []);
    const c = member(['Y'], ['Y']);
    expect(replaceState([a, b], 'X')).toBeNull();
    expect(replaceState([a, c], 'x')).toBe(true);
    expect(replaceState([a, b], 'Y')).toBe(false);
  });
});

describe('decidedReplaceLabels', () => {
  it('is undefined when the decisions leave the member as it is', () => {
    expect(
      decidedReplaceLabels(member(['X', 'Y'], ['X']), new Map([['Y', false]])),
    ).toBeUndefined();
    expect(decidedReplaceLabels(member(['X'], ['X']), new Map([['x', true]]))).toBeUndefined();
  });

  it('applies decided labels the member carries and keeps its own choice for the rest', () => {
    const decisions = new Map([
      ['Y', true],
      ['Z', true],
    ]);
    expect(decidedReplaceLabels(member(['X', 'Y'], ['X']), decisions)).toEqual(['X', 'Y']);
    expect(decidedReplaceLabels(member(['X', 'Y'], []), decisions)).toEqual(['Y']);
    expect(decidedReplaceLabels(member(['X', 'Y'], ['X', 'Y']), new Map([['X', false]]))).toEqual([
      'Y',
    ]);
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
