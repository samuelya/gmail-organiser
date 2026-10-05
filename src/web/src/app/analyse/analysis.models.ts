import { ParamMap } from '@angular/router';
import { JobDto, JobProgress, progressPercent } from '../core/jobs.models';
import { humanise } from '../dashboard/fetch.models';

/**
 * The scopes the Analyse page starts; the API also has `messages`, used by Review. `top_senders` counts senders
 * (one policy proposal each), and its sender address is optional.
 */
export type AnalysisScope = 'inbox' | 'all' | 'labelled' | 'sender' | 'top_senders';

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

/** `PolicyCandidateDto`: a sender a top senders run would propose a policy for; `scopeKey` is an address or list id. */
export interface PolicyCandidateDto {
  scope: 'sender' | 'list';
  scopeKey: string;
  displayName: string | null;
  count: number;
}

/** `GroupingPreviewDto` from `POST /api/analysis/preview`; `senders` is set for the top senders scope. */
export interface GroupingPreviewDto {
  messages: number;
  skipped: number;
  groups: number;
  estimatedLlmCalls: number;
  estimatedDerived: number;
  estimatedFromMemory: number;
  embeddingsAvailable: boolean;
  largestGroups: GroupPreviewDto[];
  senders?: PolicyCandidateDto[] | null;
}

/** `analyse` writes suggestions; `compare` stores its results as alternatives next to them (a re-analysis). */
export type AnalysisRunKind = 'analyse' | 'compare';

/** `AnalysisRunDto`; `savedPercent` is a fraction (`1 − llmCalls / max(1, messagesCovered)`) and can be negative. */
export interface AnalysisRunDto {
  id: string;
  jobId: string | null;
  kind: AnalysisRunKind;
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
  promptTokens: number;
  completionTokens: number;
  llmSeconds: number;
  /** Model calls whose prompt filled at least 90 % of the context window. */
  nearContextLimit: number;
  /** Sender policies a top senders run stored as proposed. */
  policiesProposed: number;
  /** Calls to the triage model; `escalatedCalls` of them were repeated with the chat model. */
  triageCalls: number;
  escalatedCalls: number;
  /** Queued or running, but no job is behind it any more: it can only be resumed. */
  isStalled: boolean;
  /** Emails sent in a pack of one-off senders; `packRetries` of them were asked again on their own. */
  packedMessages: number;
  packRetries: number;
}

/** The run's state badge: a stalled or failed run is the one the user can resume. */
export type RunBadge = 'stalled' | 'failed';

export function runBadge(run: Pick<AnalysisRunDto, 'status' | 'isStalled'>): RunBadge | null {
  if (run.isStalled) return 'stalled';
  return run.status === 'failed' ? 'failed' : null;
}

/** `POST /runs/{id}/resume` takes only a failed or stalled run. */
export function canResume(run: Pick<AnalysisRunDto, 'status' | 'isStalled'>): boolean {
  return runBadge(run) !== null;
}

/** Triage, escalated, packed and pack retry counts that are not zero, or `null` when all are. */
export function modelCountersText(
  run: Pick<AnalysisRunDto, 'triageCalls' | 'escalatedCalls' | 'packedMessages' | 'packRetries'>,
): string | null {
  const parts = [
    [run.triageCalls, 'triage call', 'triage calls'],
    [run.escalatedCalls, 'escalated', 'escalated'],
    [run.packedMessages, 'packed', 'packed'],
    [run.packRetries, 'pack retry', 'pack retries'],
  ] as const;
  const text = parts
    .filter(([n]) => n > 0)
    .map(([n, one, many]) => `${n.toLocaleString()} ${n === 1 ? one : many}`);
  return text.length ? text.join(' · ') : null;
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
  /** Mail with a personal label that no run has analysed yet: the labelled phase's backlog. */
  labelledNotAnalysed: number;
  /** Suggestions with a re-analysis result waiting for "Use new" or "Keep current". */
  alternatives: number;
}

/** `CompareRunRequest`: exactly one of the two. */
export type CompareRunRequest = { suggestionIds: string[] } | { runId: string };

/** `analysis_run`, the job type a run enqueues. */
export const ANALYSIS_RUN_JOB = 'analysis_run';

export const COUNT_PRESETS: readonly number[] = [10, 20, 50];
export const TOP_SENDERS: AnalysisScope = 'top_senders';
/** The API's sender range for the top senders scope; its count starts at the default. */
export const MAX_TOP_SENDERS = 100;
export const DEFAULT_TOP_SENDERS = 10;
/** Finished runs shown on the page. */
export const FINISHED_RUNS_SHOWN = 20;
/** Active runs requested: the API's maximum. It lists newest first, so a full answer may miss the oldest. */
export const ACTIVE_RUNS_LIMIT = 200;
export const MAX_SENDER_LENGTH = 320;

/** The Analyse page's scope options, in toggle order; `help` is shown under the toggle when set. */
export const SCOPE_OPTIONS: readonly { value: AnalysisScope; label: string; help?: string }[] = [
  { value: 'inbox', label: 'Inbox' },
  { value: 'all', label: 'All mail' },
  {
    value: 'labelled',
    label: 'Already labelled',
    help: 'Mail that already has a label and has not been analysed',
  },
  { value: 'sender', label: 'Sender' },
  {
    value: 'top_senders',
    label: 'Top senders',
    help: 'Proposes a policy for each of the senders and lists with the most mail',
  },
];
const SCOPES: readonly AnalysisScope[] = SCOPE_OPTIONS.map((o) => o.value);
const SCOPE_LABELS: Record<string, string> = {
  ...Object.fromEntries(SCOPE_OPTIONS.map((o) => [o.value, o.label])),
  messages: 'Selected emails',
};

export function scopeLabel(scope: string): string {
  return SCOPE_LABELS[scope] ?? humanise(scope);
}

/**
 * "Sender: news@example.com" or the plain scope label. A compare run reads "Re-analysis of n emails":
 * it covers a selection or an earlier run's emails, and the API stores both as the messages scope.
 */
export function runTarget(
  run: Pick<AnalysisRunDto, 'scope' | 'senderAddress'> &
    Partial<Pick<AnalysisRunDto, 'requestedCount'>> & { kind?: AnalysisRunKind },
): string {
  if (run.kind === 'compare') {
    const n = run.requestedCount ?? 0;
    return `Re-analysis of ${n} ${n === 1 ? 'email' : 'emails'}`;
  }
  const label = scopeLabel(run.scope);
  return run.senderAddress ? `${label}: ${run.senderAddress}` : label;
}

/** "10 senders" for a top senders run, "20 emails" otherwise: what the run's count counts. */
export function requestedText(run: Pick<AnalysisRunDto, 'scope' | 'requestedCount'>): string {
  const n = run.requestedCount;
  const unit =
    run.scope === TOP_SENDERS ? (n === 1 ? 'sender' : 'senders') : n === 1 ? 'email' : 'emails';
  return `${n.toLocaleString()} ${unit}`;
}

/** "1,200 prompt + 300 completion tokens · 12.5 s LLM", or `null` before the run made a model call. */
export function usageText(
  run: Pick<AnalysisRunDto, 'promptTokens' | 'completionTokens' | 'llmSeconds'>,
): string | null {
  if (run.promptTokens === 0 && run.completionTokens === 0 && run.llmSeconds === 0) return null;
  const seconds = run.llmSeconds.toLocaleString(undefined, { maximumFractionDigits: 1 });
  return `${run.promptTokens.toLocaleString()} prompt + ${run.completionTokens.toLocaleString()} completion tokens · ${seconds} s LLM`;
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

/** Queue order, as runs execute: running runs on top, then queued ones, each oldest first. */
export function queueOrder(runs: readonly AnalysisRunDto[]): AnalysisRunDto[] {
  const rank = (r: AnalysisRunDto) => (r.status === 'running' ? 0 : 1);
  return [...runs].sort(
    (a, b) => rank(a) - rank(b) || Date.parse(a.createdAt) - Date.parse(b.createdAt),
  );
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
