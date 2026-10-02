/** `JobProgress`: `total` is `null` while the job does not know it yet. */
export interface JobProgress {
  done: number;
  total: number | null;
  message: string | null;
}

/** Job status as the API writes it (snake_case). */
export type JobStatus = 'queued' | 'running' | 'paused' | 'completed' | 'failed' | 'cancelled';

/** `JobDto` from `/api/jobs` and the `/hubs/jobs` events. */
export interface JobDto {
  id: string;
  type: string;
  queue: string;
  status: JobStatus;
  progress: JobProgress | null;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  /** Display only: not ordered across concurrent writes; compare `version` instead. */
  updatedAt: string;
  finishedAt: string | null;
  /** Strictly increasing per job; the higher one is the newer state. */
  version: number;
}

/** Live-update connection state of the jobs hub. */
export type JobsConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** Which job controls apply to a job in this status (the API answers 409 for the others). */
export interface JobControls {
  pause: boolean;
  resume: boolean;
  cancel: boolean;
}

const ACTIVE: readonly JobStatus[] = ['queued', 'running', 'paused'];

export function isActiveJob(job: Pick<JobDto, 'status'>): boolean {
  return ACTIVE.includes(job.status);
}

export function jobControls(status: JobStatus): JobControls {
  return {
    pause: status === 'queued' || status === 'running',
    resume: status === 'paused' || status === 'failed',
    cancel: status !== 'completed' && status !== 'cancelled',
  };
}

/** The newer of two states of the same job by `version`; `a` when equal (the same state). */
export function newerJob(a: JobDto, b: JobDto | null | undefined): JobDto {
  return b && b.version > a.version ? b : a;
}

/** `done / total` as a percentage, or `null` while the total is unknown. */
export function progressPercent(progress: JobProgress | null | undefined): number | null {
  if (!progress || progress.total === null || progress.total <= 0) return null;
  return Math.min(100, Math.round((progress.done / progress.total) * 100));
}
