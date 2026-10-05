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

/** One chart point: inbox unread and % of messages covered by a policy (0–100). */
export interface TriagePoint {
  day: string;
  unread: number;
  coverage: number;
}

export function triagePoints(history: readonly MetricPointDto[]): TriagePoint[] {
  return history.map((p) => ({
    day: p.day,
    unread: p.inboxUnreadCount,
    coverage: p.messagesTotal === 0 ? 0 : (p.coveredByPolicy / p.messagesTotal) * 100,
  }));
}

/** The smallest of 1, 2, 2.5, 5 × 10ⁿ at or above `value` (at least 1), so axis ticks stay round. */
export function niceMax(value: number): number {
  if (value <= 1) return 1;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  const step = [1, 2, 2.5, 5, 10].find((s) => s * magnitude >= value) ?? 10;
  return step * magnitude;
}

/** The plot area inside the SVG, in pixels. */
export interface PlotBox {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** x of point `index` of `count`; a single point sits in the middle. */
export function xAt(index: number, count: number, box: PlotBox): number {
  return count <= 1 ? box.left + box.width / 2 : box.left + (index / (count - 1)) * box.width;
}

/** y of `value` on a 0–`max` axis. */
export function yAt(value: number, max: number, box: PlotBox): number {
  return box.top + box.height - (Math.min(value, max) / max) * box.height;
}

/** SVG path `d` through the values, one segment per point; empty for no values. */
export function linePath(values: readonly number[], max: number, box: PlotBox): string {
  return values
    .map(
      (v, i) =>
        `${i === 0 ? 'M' : 'L'}${round(xAt(i, values.length, box))},${round(yAt(v, max, box))}`,
    )
    .join(' ');
}

/** The index of the point nearest to pixel `x`. */
export function nearestIndex(x: number, count: number, box: PlotBox): number {
  if (count <= 1) return 0;
  const i = Math.round(((x - box.left) / box.width) * (count - 1));
  return Math.max(0, Math.min(count - 1, i));
}

function round(n: number): number {
  return Math.round(n * 10) / 10;
}
