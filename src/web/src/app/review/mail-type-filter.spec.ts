import { emptyGroupsText, keptSelection } from './mail-type-filter';
import { ReviewGroupDto, SuggestionDto } from './review.models';

const group = (...members: [string, string | null][]) =>
  ({
    members: members.map(([id, mailType]) => ({ id, mailType }) as SuggestionDto),
  }) as ReviewGroupDto;

describe('mail-type filter', () => {
  const groups = [group(['a', 'newsletter'], ['b', 'newsletter']), group(['c', 'receipt'])];

  it('drops ticked members the filter hides', () => {
    expect([...keptSelection(new Set(['a', 'b', 'c']), groups, ['receipt'])!]).toEqual(['c']);
    expect(keptSelection(new Set(['a', 'b']), groups, ['receipt'])!.size).toBe(0);
  });

  it('keeps the selection as it is when nothing ticked is hidden', () => {
    expect(keptSelection(new Set(['a', 'c']), groups, [])).toBeNull();
    expect(keptSelection(new Set(['a']), groups, ['newsletter'])).toBeNull();
    expect(keptSelection(new Set(), groups, ['receipt'])).toBeNull();
  });

  it('says "on this page" for an empty filtered page when other pages exist', () => {
    expect(emptyGroupsText('pending', false, ['receipt'], true)).toBe(
      'No pending suggestions of the selected mail types on this page.',
    );
    expect(emptyGroupsText('pending', true, ['receipt'], false)).toBe(
      'No pending re-analysed suggestions of the selected mail types for this sender.',
    );
    expect(emptyGroupsText('approved', false, [], true)).toBe(
      'No approved suggestions for this sender.',
    );
  });
});
