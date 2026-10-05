import { emptyGroupsText, listedKey } from './mail-type-filter';

describe('mail-type filter', () => {
  it('names the selected mail types in the empty state', () => {
    expect(emptyGroupsText('pending', true, ['receipt'])).toBe(
      'No pending re-analysed suggestions of the selected mail types for this sender.',
    );
    expect(emptyGroupsText('approved', false, [])).toBe('No approved suggestions for this sender.');
  });

  it('keys a senders list by tab, Re-analysed and mail types', () => {
    expect(listedKey('pending', false, [])).not.toBe(listedKey('pending', false, ['receipt']));
    expect(listedKey('pending', true, ['receipt'])).toBe('pending|true|receipt');
  });
});
