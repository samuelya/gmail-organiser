import { formatDate, formatNumber } from '@angular/common';
import { ANALYSIS_RUN_JOB, AnalysisRunDto, runTarget } from '../analyse/analysis.models';
import { ChartTable } from '../core/charts/chart.models';
import { JobDto } from '../core/jobs.models';
import { hoursText } from './triage.models';

/** How many runs the chart shows. */
export const RUN_COST_RUNS = 20;

/**
 * Completed runs asked of the API: its most, since compare runs are dropped here
 * (the list has no kind filter or paging).
 */
export const RUN_COST_FETCH = 200;

/** The x label and table format for a run's `finishedAt`. */
export const RUN_COST_DATE = 'd MMM HH:mm';

/** One bar: where a completed analyse run's messages went and how long the model took. */
export interface RunCost {
  id: string;
  finishedAt: string;
  /** `runTarget(run)`: the scope, and the sender of a sender run. */
  target: string;
  llm: number;
  derived: number;
  memory: number;
  llmSeconds: number;
}

/** The last `RUN_COST_RUNS` completed analyse runs, oldest first; compare, failed and cancelled runs are left out. */
export function runCosts(runs: readonly AnalysisRunDto[]): RunCost[] {
  return runs
    .filter((r) => r.kind === 'analyse' && r.status === 'completed' && r.finishedAt !== null)
    .sort((a, b) => Date.parse(a.finishedAt!) - Date.parse(b.finishedAt!))
    .slice(-RUN_COST_RUNS)
    .map((r) => ({
      id: r.id,
      finishedAt: r.finishedAt!,
      target: runTarget(r),
      llm: r.messagesLlm,
      derived: r.messagesDerived,
      memory: r.messagesFromMemory,
      llmSeconds: r.llmSeconds,
    }));
}

/** The share of the rows' messages that needed no model call (derived or from memory); null when none were covered. */
export function savedShare(rows: readonly RunCost[]): number | null {
  const sum = (pick: (r: RunCost) => number) => rows.reduce((total, r) => total + pick(r), 0);
  const spared = sum((r) => r.derived + r.memory);
  const total = spared + sum((r) => r.llm);
  return total === 0 ? null : spared / total;
}

/** "62%" for a `savedShare`. */
export function savedText(share: number, locale: string): string {
  return `${formatNumber(share * 100, locale, '1.0-0')}%`;
}

/** Changes when an analysis job reaches a terminal status: only then can a completed run appear. */
export function endedAnalysisJobsKey(jobs: readonly JobDto[]): string {
  return jobs
    .filter(
      (j) =>
        j.type === ANALYSIS_RUN_JOB &&
        (j.status === 'completed' || j.status === 'failed' || j.status === 'cancelled'),
    )
    .map((j) => `${j.id}:${j.status}`)
    .sort()
    .join(',');
}

/** "Over the last 20 runs, 62% of messages needed no model call; model time 14 min." */
export function runCostSummary(rows: readonly RunCost[], locale: string): string {
  if (rows.length === 0) return 'No analysis runs yet.';
  const time = hoursText(rows.reduce((total, r) => total + r.llmSeconds, 0) / 3600);
  const over = rows.length === 1 ? 'In the last run' : `Over the last ${rows.length} runs`;
  const share = savedShare(rows);
  if (share === null) return `${over}, no messages were covered; model time ${time}.`;
  return `${over}, ${savedText(share, locale)} of messages needed no model call; model time ${time}.`;
}

/** The same runs as a table, oldest first like the bars. */
export function runCostTable(rows: readonly RunCost[], locale: string): ChartTable {
  return {
    columns: ['Finished', 'Target', 'By model', 'Derived', 'From memory', 'Model seconds'],
    rows: rows.map((r) => [
      formatDate(r.finishedAt, RUN_COST_DATE, locale),
      r.target,
      r.llm,
      r.derived,
      r.memory,
      Math.round(r.llmSeconds * 10) / 10,
    ]),
  };
}
