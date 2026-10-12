import {
  categorySlices,
  categorySummary,
  categoryTable,
  MailboxBreakdownDto,
  mailTypeBars,
  mailTypeSummary,
  mailTypeTable,
} from './breakdown.models';

const zero = { primary: 0, promotions: 0, social: 0, updates: 0, forums: 0 };

function breakdown(overrides: Partial<MailboxBreakdownDto> = {}): MailboxBreakdownDto {
  return {
    categories: { primary: 30, promotions: 48, social: 2, updates: 15, forums: 5 },
    mailTypes: [
      { mailType: 'personal', count: 40 },
      { mailType: 'receipt', count: 0 },
      { mailType: 'newsletter', count: 120 },
      { mailType: 'action_bill', count: 40 },
    ],
    unclassified: 7,
    ...overrides,
  };
}

describe('breakdown models', () => {
  it('builds all five category slices in tab order with their share', () => {
    const slices = categorySlices(breakdown().categories);

    expect(slices.map((s) => [s.label, s.count, s.percent])).toEqual([
      ['Primary', 30, 30],
      ['Promotions', 48, 48],
      ['Social', 2, 2],
      ['Updates', 15, 15],
      ['Forums', 5, 5],
    ]);
    expect(categorySlices(zero)).toEqual([]);
  });

  it('names the largest category in the summary and lists shares in the table', () => {
    const slices = categorySlices({ ...zero, primary: 1, promotions: 2 });

    expect(categorySummary(slices)).toBe('Most of your mail is in Promotions (67%).');
    expect(categoryTable(slices).rows[0]).toEqual(['Primary', 1, 33.3]);
    expect(categoryTable(slices).columns).toEqual(['Category', 'Messages', 'Share %']);
  });

  it('keeps types with suggestions, most first and ties by label, then Unclassified', () => {
    const bars = mailTypeBars(breakdown());

    expect(bars.map((b) => [b.label, b.count, b.classified])).toEqual([
      ['newsletter', 120, true],
      ['action bill', 40, true],
      ['personal', 40, true],
      ['Unclassified', 7, false],
    ]);
    expect(mailTypeBars(breakdown({ unclassified: 0 })).at(-1)!.label).toBe('personal');
    expect(mailTypeBars(breakdown({ mailTypes: [], unclassified: 0 }))).toEqual([]);
  });

  it('links each typed bar to its pending suggestions; Unclassified and unknown types go nowhere', () => {
    const bars = mailTypeBars(
      breakdown({
        mailTypes: [
          { mailType: 'newsletter', count: 3 },
          { mailType: 'not_a_type', count: 1 },
        ],
      }),
    );

    expect(bars[0].link).toEqual({
      text: 'newsletter',
      link: '/review',
      queryParams: { mailType: 'newsletter', status: 'pending' },
    });
    expect(bars[1].link).toBeNull();
    expect(bars[2].link).toBeNull();
  });

  it('puts the same links in the table', () => {
    const table = mailTypeTable(mailTypeBars(breakdown()));

    expect(table.columns).toEqual(['Mail type', 'Suggestions']);
    expect(table.rows[0]).toEqual([
      {
        text: 'newsletter',
        link: '/review',
        queryParams: { mailType: 'newsletter', status: 'pending' },
      },
      120,
    ]);
    expect(table.rows.at(-1)).toEqual(['Unclassified', 7]);
  });

  it('names the commonest type out of the classified suggestions', () => {
    expect(mailTypeSummary(mailTypeBars(breakdown()), 'en-US')).toBe(
      'Newsletter is the most common mail type (120 of 200 classified).',
    );
    expect(
      mailTypeSummary(
        mailTypeBars(breakdown({ mailTypes: [{ mailType: 'newsletter', count: 1200 }] })),
        'en-US',
      ),
    ).toBe('Newsletter is the most common mail type (1,200 of 1,200 classified).');
    expect(mailTypeSummary(mailTypeBars(breakdown({ mailTypes: [] })), 'en-US')).toBe(
      'None of the 7 suggestions has a mail type yet.',
    );
  });
});
