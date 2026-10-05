import {
  hoursText,
  countTicks,
  linePath,
  MetricPointDto,
  nearestIndex,
  niceMax,
  percentText,
  PlotBox,
  triagePoints,
  xAt,
} from './triage.models';

const box: PlotBox = { left: 10, top: 5, width: 100, height: 50 };

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

  it('triagePoints derives % covered by policy, 0 for an empty mailbox', () => {
    expect(
      triagePoints([point(), point({ day: '2026-01-02', messagesTotal: 0, coveredByPolicy: 0 })]),
    ).toEqual([
      { day: '2026-01-01', unread: 12, coverage: 25 },
      { day: '2026-01-02', unread: 12, coverage: 0 },
    ]);
  });

  it('niceMax rounds up to 1, 2, 2.5 or 5 × 10ⁿ', () => {
    expect(niceMax(0)).toBe(1);
    expect(niceMax(7)).toBe(10);
    expect(niceMax(13)).toBe(20);
    expect(niceMax(240)).toBe(250);
    expect(niceMax(4100)).toBe(5000);
    expect(niceMax(500)).toBe(500);
  });

  it('niceMax skips 2.5 below ten so the unread axis stays whole', () => {
    expect(niceMax(2.2)).toBe(5);
    expect(niceMax(24)).toBe(25);
  });

  it('countTicks keeps the middle tick only when it is a whole number', () => {
    expect(countTicks(1)).toEqual([0, 1]);
    expect(countTicks(5)).toEqual([0, 5]);
    expect(countTicks(25)).toEqual([0, 25]);
    expect(countTicks(10)).toEqual([0, 5, 10]);
    expect(countTicks(2)).toEqual([0, 1, 2]);
  });
});

describe('triage chart geometry', () => {
  it('linePath draws a fixed series across the plot box', () => {
    expect(linePath([0, 5, 10], 10, box)).toBe('M10,55 L60,30 L110,5');
  });

  it('linePath clamps values above the axis maximum and is empty without values', () => {
    expect(linePath([20, 0], 10, box)).toBe('M10,5 L110,55');
    expect(linePath([], 10, box)).toBe('');
  });

  it('a single point sits in the middle', () => {
    expect(xAt(0, 1, box)).toBe(60);
    expect(linePath([5], 10, box)).toBe('M60,30');
  });

  it('nearestIndex snaps a pixel to the closest day and clamps outside the plot', () => {
    expect(nearestIndex(10, 5, box)).toBe(0);
    expect(nearestIndex(48, 5, box)).toBe(2);
    expect(nearestIndex(-40, 5, box)).toBe(0);
    expect(nearestIndex(500, 5, box)).toBe(4);
    expect(nearestIndex(500, 1, box)).toBe(0);
  });
});
