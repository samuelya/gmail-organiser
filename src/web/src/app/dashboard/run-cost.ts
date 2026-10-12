import { formatDate, formatNumber } from '@angular/common';
import { AnalysisRunDto, runTarget } from '../analyse/analysis.models';
import { ChartTable } from '../core/charts/chart.models';

/** How many runs the chart shows. */
export const RUN_COST_RUNS = 20;

/** Runs asked of the API: compare runs are dropped here, so ask for more than the chart shows. */
export const RUN_COST_FETCH = 40;

/** The x label and table format for a run's `finishedAt`. */
export const RUN_COST_DATE = 'd MMM HH:mm';

/** One bar: where a finished analyse run's messages went and how long the model took. */
export interface RunCost {
  id: string;
  finishedAt: string;
  /** `runTarget(run)`: the scope, and the sender of a sender run. */
  target: string;
  llm: number;
  derived: number;
  memory: number;
  llmSeconds: number;
  /** A fraction; negative when the run made more calls than it covered messages. */
  savedPercent: number;
}

/** The last `RUN_COST_RUNS` finished analyse runs, oldest first; compare and unfinished runs are left out. */
export function runCosts(runs: readonly AnalysisRunDto[]): RunCost[] {
  return runs
    .filter((r) => r.kind === 'analyse' && r.finishedAt !== null)
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
      savedPercent: r.savedPercent,
    }));
}

/** "45 s", "14 min" or "2 h 5 min". */
export function durationText(seconds: number): string {
  const s = Math.round(seconds);
  if (s < 60) return `${s} s`;
  const minutes = Math.round(s / 60);
  if (minutes < 60) return `${minutes} min`;
  const rest = minutes % 60;
  return rest ? `${Math.floor(minutes / 60)} h ${rest} min` : `${minutes / 60} h`;
}

/** "Over the last 20 runs, 62% of messages needed no model call; model time 14 min." */
export function runCostSummary(rows: readonly RunCost[], locale: string): string {
  if (rows.length === 0) return 'No analysis runs yet.';
  const sum = (pick: (r: RunCost) => number) => rows.reduce((total, r) => total + pick(r), 0);
  const llm = sum((r) => r.llm);
  const spared = sum((r) => r.derived + r.memory);
  const time = durationText(sum((r) => r.llmSeconds));
  const over = rows.length === 1 ? 'In the last run' : `Over the last ${rows.length} runs`;
  if (llm + spared === 0) return `${over}, no messages were covered; model time ${time}.`;
  const share = formatNumber((spared / (llm + spared)) * 100, locale, '1.0-0');
  return `${over}, ${share}% of messages needed no model call; model time ${time}.`;
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
