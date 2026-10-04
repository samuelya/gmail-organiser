import {
  alternativeDiff,
  alternativeMessage,
  alternativeRequest,
  alternativeSource,
  CompareValues,
  memberValues,
  repends,
  SuggestionAlternativeDto,
} from './alternative.models';
import { ReviewGroupDto, SuggestionDto } from './review.models';

const alternative = (over: Partial<SuggestionAlternativeDto> = {}): SuggestionAlternativeDto => ({
  topicLabel: 'Topic/Alpha',
  documentTypeLabel: null,
  replaceLabels: [],
  labelChange: 'add',
  needsAction: false,
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

const current: CompareValues = {
  topicLabel: 'Topic/Alpha',
  documentTypeLabel: null,
  labelChange: 'add',
  replaceLabels: [],
  needsAction: false,
  toBeDeleted: false,
  confidenceMin: 0.7,
  confidenceMax: 0.9,
};

const member = (over: Partial<SuggestionDto> = {}) =>
  ({
    id: 's-1',
    status: 'pending',
    confidence: 0.8,
    alternative: alternative(),
    ...over,
  }) as SuggestionDto;

describe('alternative models', () => {
  it('finds no difference when the outcome is the same and the confidence lies in the range', () => {
    expect(Object.values(alternativeDiff(current, alternative())).some((d) => d)).toBe(false);
  });

  it('flags each differing field on its own', () => {
    const diff = alternativeDiff(
      current,
      alternative({
        topicLabel: 'Topic/Beta',
        documentTypeLabel: 'Type/Invoice',
        needsAction: true,
        toBeDeleted: true,
        confidence: 0.95,
      }),
    );
    expect(diff).toEqual({
      topicLabel: true,
      documentType: true,
      labelChange: true,
      needsAction: true,
      toBeDeleted: true,
      confidence: true,
    });
  });

  it('compares the label change as shown, including the replaced labels', () => {
    const relabel = { ...current, labelChange: 'relabel' as const, replaceLabels: ['Old/One'] };
    expect(
      alternativeDiff(relabel, alternative({ labelChange: 'relabel', replaceLabels: ['Old/One'] }))
        .labelChange,
    ).toBe(false);
    expect(
      alternativeDiff(relabel, alternative({ labelChange: 'relabel', replaceLabels: ['Old/Two'] }))
        .labelChange,
    ).toBe(true);
  });

  it("a member's confidence differs when the shown percentages differ", () => {
    const m = memberValues(member({ confidence: 0.8 }) as SuggestionDto);
    expect(alternativeDiff(m, alternative({ confidence: 0.801 })).confidence).toBe(false);
    expect(alternativeDiff(m, alternative({ confidence: 0.81 })).confidence).toBe(true);
  });

  it('names the prompt version and model when known', () => {
    expect(alternativeSource(alternative())).toBe('Prompt v9 · model-a');
    expect(alternativeSource(alternative({ promptVersion: null, model: null }))).toBe('');
  });

  it('a member goes by id, a group card by sender, key and the viewed status', () => {
    const m = member();
    expect(alternativeRequest({ member: m }, 'news@example.com', 'approved')).toEqual({
      suggestionIds: ['s-1'],
    });
    const group = { groupKey: 'key-1', members: [m] } as ReviewGroupDto;
    expect(alternativeRequest({ group }, 'news@example.com', 'approved')).toEqual({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1', status: 'approved' }],
    });
    const single = { groupKey: null, members: [m] } as unknown as ReviewGroupDto;
    expect(alternativeRequest({ group: single }, 'news@example.com', 'pending')).toEqual({
      suggestionIds: ['s-1'],
    });
  });

  it('only approved or applied suggestions with a result become pending again', () => {
    expect(repends({ member: member() })).toBe(false);
    expect(repends({ member: member({ status: 'applied' }) })).toBe(true);
    const group = {
      members: [member({ status: 'approved', alternative: null }), member({ status: 'approved' })],
    } as ReviewGroupDto;
    expect(repends({ group })).toBe(true);
  });

  it('reports what was decided and what an active apply kept', () => {
    expect(alternativeMessage('accept', { accepted: 1, discarded: 0, skipped: 0 })).toBe(
      'Used the new result for 1 suggestion.',
    );
    expect(alternativeMessage('discard', { accepted: 0, discarded: 3, skipped: 2 })).toBe(
      'Kept the current result for 3 suggestions. 2 skipped: part of an apply that is still running.',
    );
  });
});
