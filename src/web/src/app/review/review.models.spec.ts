import { convertToParamMap } from '@angular/router';
import { parseReviewParams } from './review.models';

describe('parseReviewParams', () => {
  const parse = (params: Record<string, string>) => parseReviewParams(convertToParamMap(params));

  it('reads a known status and a trimmed sender', () => {
    expect(parse({ status: 'applied', sender: ' News@Example.com ' })).toEqual({
      status: 'applied',
      sender: 'News@Example.com',
    });
  });

  it('falls back to pending with no sender when the params are missing or unknown', () => {
    expect(parse({})).toEqual({ status: 'pending', sender: null });
    expect(parse({ status: 'deleted', sender: '   ' })).toEqual({
      status: 'pending',
      sender: null,
    });
  });

  it('ignores a sender longer than 320 characters', () => {
    const local = 'a'.repeat(308);
    expect(parse({ sender: `${local}@example.com` }).sender).toBe(`${local}@example.com`);
    expect(parse({ sender: `${local}x@example.com` }).sender).toBeNull();
  });
});
