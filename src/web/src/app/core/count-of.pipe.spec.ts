import { countOf, percentOf } from './count-of.pipe';

describe('countOf', () => {
  it.each([
    [3200, 12450, '3,200 of 12,450 · 25%'],
    [999, 1000, '999 of 1,000 · 99%'],
    [120, 100, '120 of 100 · 100%'],
    [0, 0, '0 of 0'],
    [3200, null, '3,200'],
    [3200, undefined, '3,200'],
  ] as const)('%s of %s reads %s', (done, total, text) => {
    expect(countOf(done, total, 'en-US')).toBe(text);
  });

  it('percentOf rounds down and caps at 100', () => {
    expect(percentOf(2, 3)).toBe(66);
    expect(percentOf(5, 4)).toBe(100);
    expect(percentOf(1, null)).toBeNull();
  });
});
