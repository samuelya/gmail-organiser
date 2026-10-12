import {
  hoursText,
  MetricPointDto,
  percentText,
  triageChartSummary,
  triageChartTable,
  TriagePoint,
  triagePoints,
} from './triage.models';

const point = (over: Partial<MetricPointDto> = {}): MetricPointDto => ({
  day: '2026-01-01',
  takenAt: '2026-01-01T23:00:00Z',
  messagesTotal: 200,
  inboxCount: 40,
  inboxUnreadCount: 12,
  coveredByPolicy: 50,
  coveredByFilter: 20,
  analysed: 100,
  applied: 30,
  toBeDeleted: 5,
  llmHours: 0.5,
  ...over,
});

describe('triage figures', () => {
  it('percentText shows one decimal', () => {
    expect(percentText(0)).toBe('0.0%');
    expect(percentText(0.425)).toBe('42.5%');
    expect(percentText(1 / 3)).toBe('33.3%');
    expect(percentText(1)).toBe('100.0%');
  });

  it('hoursText shows minutes under an hour, hours with one decimal above', () => {
    expect(hoursText(0)).toBe('0 min');
    expect(hoursText(0.25)).toBe('15 min');
    expect(hoursText(1)).toBe('1.0 h');
    expect(hoursText(12.345)).toBe('12.3 h');
  });

  it('triagePoints derives % covered by policy and by filter, 0 for an empty mailbox', () => {
    expect(
      triagePoints([
        point(),
        point({ day: '2026-01-02', messagesTotal: 0, coveredByPolicy: 0, coveredByFilter: 0 }),
      ]),
    ).toEqual([
      { day: '2026-01-01', unread: 12, policyCoverage: 25, filterCoverage: 10 },
      { day: '2026-01-02', unread: 12, policyCoverage: 0, filterCoverage: 0 },
    ]);
  });
});

const points: TriagePoint[] = [
  { day: '2026-01-01', unread: 1200, policyCoverage: 10, filterCoverage: 2.04 },
  { day: '2026-01-02', unread: 900, policyCoverage: 30, filterCoverage: 4 },
  { day: '2026-01-03', unread: 85, policyCoverage: 42.46, filterCoverage: 5.5 },
];

describe('triage chart text', () => {
  it('summarises the first and the last point', () => {
    expect(triageChartSummary(points, 'en-US')).toBe(
      'Inbox unread went from 1,200 to 85, policy coverage from 10% to 42% ' +
        'and filter coverage from 2% to 6% over 3 days.',
    );
  });

  it('summarises a single day and no history', () => {
    expect(triageChartSummary(points.slice(0, 1), 'en-US')).toBe(
      'Inbox unread is 1,200, policy coverage 10% and filter coverage 2% on the first day.',
    );
    expect(triageChartSummary([], 'en-US')).toBe('No history yet.');
  });

  it('builds one table row per day with percentages to one decimal', () => {
    expect(triageChartTable(points, 'en-US')).toEqual({
      columns: ['Day', 'Inbox unread', 'Covered by policy %', 'Covered by filter %'],
      rows: [
        ['Jan 1, 2026', 1200, 10, 2],
        ['Jan 2, 2026', 900, 30, 4],
        ['Jan 3, 2026', 85, 42.5, 5.5],
      ],
    });
    expect(triageChartTable([], 'en-US').rows).toEqual([]);
  });
});
