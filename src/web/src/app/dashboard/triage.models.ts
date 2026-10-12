import { formatDate, formatNumber } from '@angular/common';
import { ChartTable } from '../core/charts/chart.models';

/** `GET /api/dashboard/triage`: one snapshot with its coverage ratios (0–1; 0 when the denominator is 0). */
export interface MetricSnapshotDto {
  takenAt: string;
  messagesTotal: number;
  inboxCount: number;
  inboxUnreadCount: number;
  coveredByPolicy: number;
  coveredByFilter: number;
  analysed: number;
  applied: number;
  toBeDeleted: number;
  llmMillisecondsTotal: number;
  promptTokensTotal: number;
  policyCoverageRatio: number;
  filterCoverageRatio: number;
  analysedRatio: number;
  inboxUnreadRatio: number;
}

/** The last snapshot of one UTC day. */
export interface MetricPointDto {
  day: string;
  takenAt: string;
  messagesTotal: number;
  inboxCount: number;
  inboxUnreadCount: number;
  coveredByPolicy: number;
  coveredByFilter: number;
  analysed: number;
  applied: number;
  toBeDeleted: number;
  llmHours: number;
}

/** `current` is null before the first snapshot; `history` is one point per day, oldest first. */
export interface TriageMetricsDto {
  current: MetricSnapshotDto | null;
  history: MetricPointDto[];
  llmHours: number;
}

/** A ratio (0–1) as a percentage with one decimal, e.g. `42.5%`. */
export function percentText(ratio: number): string {
  return `${(Math.round(ratio * 1000) / 10).toFixed(1)}%`;
}

/** LLM time: minutes under an hour, otherwise hours with one decimal. */
export function hoursText(hours: number): string {
  if (hours < 1) return `${Math.round(hours * 60)} min`;
  return `${(Math.round(hours * 10) / 10).toFixed(1)} h`;
}

/** One chart point: inbox unread and % of messages covered by a policy and by a filter (0–100). */
export interface TriagePoint {
  day: string;
  unread: number;
  policyCoverage: number;
  filterCoverage: number;
}

export function triagePoints(history: readonly MetricPointDto[]): TriagePoint[] {
  return history.map((p) => ({
    day: p.day,
    unread: p.inboxUnreadCount,
    policyCoverage: percentOf(p.coveredByPolicy, p.messagesTotal),
    filterCoverage: percentOf(p.coveredByFilter, p.messagesTotal),
  }));
}

/** First vs last point, numbers formatted for `locale`: the chart's text alternative. */
export function triageChartSummary(points: readonly TriagePoint[], locale: string): string {
  const first = points[0];
  const last = points.at(-1);
  if (!first || !last) return 'No history yet.';
  const count = (n: number) => formatNumber(n, locale, '1.0-0');
  const percent = (n: number) => `${formatNumber(n, locale, '1.0-1')}%`;
  if (points.length === 1) {
    return `Inbox unread is ${count(last.unread)}, policy coverage ${percent(last.policyCoverage)} and filter coverage ${percent(last.filterCoverage)} on the first day.`;
  }
  return (
    `Inbox unread went from ${count(first.unread)} to ${count(last.unread)}, ` +
    `policy coverage from ${percent(first.policyCoverage)} to ${percent(last.policyCoverage)} ` +
    `and filter coverage from ${percent(first.filterCoverage)} to ${percent(last.filterCoverage)} ` +
    `over ${points.length} days.`
  );
}

/** The same points as a table, one row per day, percentages to one decimal. */
export function triageChartTable(points: readonly TriagePoint[], locale: string): ChartTable {
  return {
    columns: ['Day', 'Inbox unread', 'Covered by policy %', 'Covered by filter %'],
    rows: points.map((p) => [
      formatDate(p.day, 'mediumDate', locale),
      p.unread,
      oneDecimal(p.policyCoverage),
      oneDecimal(p.filterCoverage),
    ]),
  };
}

/** Clamped to 100: inconsistent snapshot counts must not push a point off the 0–100 axis. */
function percentOf(part: number, total: number): number {
  return total === 0 ? 0 : Math.min((part / total) * 100, 100);
}

function oneDecimal(n: number): number {
  return Math.round(n * 10) / 10;
}
