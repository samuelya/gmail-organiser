import { ParamMap } from '@angular/router';
import { JobDto, JobProgress, progressPercent } from '../core/jobs.models';
import { humanise } from '../dashboard/fetch.models';

/** The scopes the Analyse page starts; the API also has `messages`, used by Review. */
export type AnalysisScope = 'inbox' | 'all' | 'sender';

/** `AnalysisRunStatus` as the API writes it; a paused job's run stays `running`. */
export type AnalysisRunStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled';

/** `AnalysisPreviewRequest` and `StartAnalysisRunRequest` (the fields the page sends). */
export interface AnalysisSelection {
  scope: AnalysisScope;
  senderAddress?: string;
  count: number;
}

/** `GroupPreviewDto`: `display` is the API's label for the group; the web never derives one. */
export interface GroupPreviewDto {
  key: string;
  senderAddress: string;
  display: string;
  size: number;
  representatives: number;
}

/** `GroupingPreviewDto` from `POST /api/analysis/preview`. */
export interface GroupingPreviewDto {
  messages: number;
  skipped: number;
  groups: number;
  estimatedLlmCalls: number;
  estimatedDerived: number;
  estimatedFromMemory: number;
  embeddingsAvailable: boolean;
  largestGroups: GroupPreviewDto[];
}

/** `AnalysisRunDto`; `savedPercent` is a fraction (`1 − llmCalls / max(1, messagesCovered)`) and can be negative. */
export interface AnalysisRunDto {
  id: string;
  jobId: string | null;
  scope: string;
  senderAddress: string | null;
  requestedCount: number;
  groupingMode: string;
  status: AnalysisRunStatus;
  messagesCovered: number;
  messagesLlm: number;
  messagesDerived: number;
  messagesFromMemory: number;
  llmCalls: number;
  groups: number;
  mixedGroups: number;
  failedMessages: number;
  skippedMessages: number;
  model: string | null;
  promptVersion: string | null;
  error: string | null;
  savedPercent: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

/** `AnalysisSummaryDto` from `GET /api/analysis/summary`. */
export interface AnalysisSummaryDto {
  notAnalysed: number;
  analysed: number;
  approved: number;
  rejected: number;
  applied: number;
  actionCount: number;
  toBeDeletedCount: number;
  totalLlmCalls: number;
  totalMessagesCovered: number;
  savedPercent: number;
}

/** `analysis_run`, the job type a run enqueues. */
export const ANALYSIS_RUN_JOB = 'analysis_run';

export const COUNT_PRESETS: readonly number[] = [10, 20, 50];
/** Finished runs shown on the page. */
export const FINISHED_RUNS_SHOWN = 20;
/** Active runs requested; more than this can't be queued in practice. */
export const ACTIVE_RUNS_LIMIT = 50;
export const MAX_SENDER_LENGTH = 320;

const SCOPES: readonly AnalysisScope[] = ['inbox', 'all', 'sender'];
const SCOPE_LABELS: Record<string, string> = {
  inbox: 'Inbox',
  all: 'All mail',
  sender: 'Sender',
  messages: 'Selected emails',
};

export function scopeLabel(scope: string): string {
  return SCOPE_LABELS[scope] ?? humanise(scope);
}

/** "Sender: news@example.com" or the plain scope label. */
export function runTarget(run: Pick<AnalysisRunDto, 'scope' | 'senderAddress'>): string {
  const label = scopeLabel(run.scope);
  return run.senderAddress ? `${label}: ${run.senderAddress}` : label;
}

/**
 * "X LLM calls for Y emails (Z % saved)". Retries count as calls, so a run can make more calls than it
 * covers emails: that reads as "Z % more calls than emails" rather than a negative saving.
 */
export function savingsText(llmCalls: number, covered: number, savedPercent: number): string {
  const calls = `${llmCalls.toLocaleString()} LLM ${llmCalls === 1 ? 'call' : 'calls'}`;
  const emails = `${covered.toLocaleString()} ${covered === 1 ? 'email' : 'emails'}`;
  if (covered === 0) return `${calls}, no emails covered`;
  const percent = Math.round(savedPercent * 100);
  return percent >= 0
    ? `${calls} for ${emails} (${percent} % saved)`
    : `${calls} for ${emails} (${-percent} % more calls than emails)`;
}

/** The page's selection from `/analyse?sender=<address>&count=<n>`; anything invalid is dropped. */
export function parseAnalyseParams(params: ParamMap): {
  sender: string | null;
  count: number | null;
} {
  const sender = params.get('sender')?.trim() ?? '';
  const count = Number(params.get('count'));
  return {
    sender: sender && sender.length <= MAX_SENDER_LENGTH ? sender : null,
    count: Number.isInteger(count) && count >= 1 && count <= 1000 ? count : null,
  };
}

export function isScope(value: unknown): value is AnalysisScope {
  return SCOPES.includes(value as AnalysisScope);
}

/** What the run list shows for an active run: the live job wins over the run's own status. */
export interface ActiveRunView {
  run: AnalysisRunDto;
  status: string;
  progress: JobProgress | null;
  percent: number | null;
}

export function activeRunView(run: AnalysisRunDto, live: JobDto | undefined): ActiveRunView {
  const status = live?.status === 'paused' ? 'paused' : run.status;
  const progress = live?.progress ?? null;
  return { run, status, progress, percent: progressPercent(progress) };
}

/** Changes when an analysis job appears or changes status: the run lists reload then. */
export function analysisJobsKey(jobs: readonly JobDto[]): string {
  return jobs
    .filter((j) => j.type === ANALYSIS_RUN_JOB)
    .map((j) => `${j.id}:${j.status}`)
    .sort()
    .join(',');
}
