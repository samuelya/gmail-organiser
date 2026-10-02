import { JobControls, jobControls, JobDto, newerJob } from '../core/jobs.models';

/** Mailbox fetch phase as the API writes it; open, so a phase added later (e.g. `reconcile`) still shows. */
export type MailboxPhase = 'not_started' | 'inbox' | 'all_mail' | 'completed' | (string & {});

/** `FetchStatusDto` from `GET /api/fetch/status`. */
export interface FetchStatusDto {
  accountEmail: string | null;
  mailboxPhase: MailboxPhase;
  inboxFetched: number;
  allMailFetched: number;
  /** Messages Gmail reports in the Inbox; `null` until known. */
  inboxTotal?: number | null;
  /** Messages Gmail reports in All Mail; `null` until known. */
  allMailTotal?: number | null;
  messagesTotal: number | null;
  messagesStored: number;
  sendersCount: number;
  lastHistoryId: string | null;
  startedAt: string | null;
  completedAt: string | null;
  /** The queued, running or paused fetch job, whichever kind it is. */
  activeJob: JobDto | null;
  /** The latest fetch job when it failed, with its error; Start resumes it. */
  failedJob: JobDto | null;
  /** The local data belongs to another account than the connected one; fetching is blocked. */
  accountMismatch: boolean;
  /** The local data's account, masked; set only while mismatched. */
  localAccount: string | null;
}

/** `StartFetchResponse`. */
export interface StartFetchResponse {
  jobId: string;
}

/** Jobs on this queue change the fetch status. */
export const FETCH_QUEUE = 'fetch';

const PHASE_LABELS: Record<string, string> = {
  not_started: 'Not started',
  inbox: 'Inbox',
  all_mail: 'All mail',
  completed: 'Completed',
};

/** Known phases read as written in the design; any other `snake_case` phase is humanised. */
export function phaseLabel(phase: MailboxPhase): string {
  return PHASE_LABELS[phase] ?? humanise(phase);
}

/** `snake_case` → "Snake case", for API values the web has no label for. */
export function humanise(value: string): string {
  const words = value.replaceAll('_', ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** What the "Mailbox fetch" card shows and allows. */
export interface FetchView {
  phase: string;
  /** The job to show progress for and control: the live copy of the active job, if any. */
  job: JobDto | null;
  /** `null` hides Start; otherwise its label. */
  startLabel: string | null;
  startDisabled: boolean;
  controls: JobControls;
  /** The last job error to show as a warning. */
  error: string | null;
}

const NO_CONTROLS: JobControls = { pause: false, resume: false, cancel: false };

/**
 * A job's controls on the dashboard: an account mismatch blocks resuming a fetch-queue job, which
 * would carry on reading the newly connected account into the other account's data.
 */
export function dashboardControls(job: JobDto, accountMismatch: boolean): JobControls {
  const controls = jobControls(job.status);
  return accountMismatch && job.queue === FETCH_QUEUE ? { ...controls, resume: false } : controls;
}

/**
 * Derives the card from the status and the hub's copy of its active job. The status says which job
 * is active (mailbox or incremental fetch); the hub copy wins when its `version` is newer.
 */
export function fetchView(status: FetchStatusDto, live: JobDto | undefined): FetchView {
  const active = status.activeJob ? newerJob(status.activeJob, live) : null;
  const blocked = status.accountMismatch;
  let startLabel: string | null = null;
  if (!active) {
    if (status.failedJob) startLabel = 'Resume fetch';
    else if (status.mailboxPhase === 'completed') startLabel = 'Fetch new mail';
    else startLabel = 'Start fetch';
  }
  return {
    phase: phaseLabel(status.mailboxPhase),
    job: active,
    startLabel,
    startDisabled: blocked,
    // A failed job resumes through Start, so its own controls stay hidden. A mismatch blocks resuming.
    controls: active
      ? { ...jobControls(active.status), ...(blocked && { resume: false }) }
      : NO_CONTROLS,
    error: active?.error ?? status.failedJob?.error ?? null,
  };
}

/** `id:done` of every running fetch job; changes on each progress tick. */
export function fetchProgressKey(jobs: readonly JobDto[]): string {
  return jobs
    .filter((j) => j.queue === FETCH_QUEUE && j.status === 'running')
    .map((j) => `${j.id}:${j.progress?.done ?? 0}`)
    .sort()
    .join(',');
}

/**
 * Changes when a fetch job appears, goes, changes status or moves to another step (its progress
 * message, e.g. Inbox to All mail); `done` ticks leave it equal.
 */
export function fetchJobsKey(jobs: readonly JobDto[]): string {
  return jobs
    .filter((j) => j.queue === FETCH_QUEUE)
    .map((j) => `${j.id}:${j.status}:${j.progress?.message ?? ''}`)
    .sort()
    .join(',');
}
