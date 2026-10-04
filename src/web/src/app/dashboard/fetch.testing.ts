import { JobDto, JobStatus } from '../core/jobs.models';
import { FetchStatusDto } from './fetch.models';

/** Synthetic fixtures for the dashboard specs. */
export const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: 'mailbox_fetch',
  queue: 'fetch',
  status,
  progress: { done: 25, total: 100, message: null },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

export const status = (over: Partial<FetchStatusDto> = {}): FetchStatusDto => ({
  accountEmail: 'user@example.com',
  mailboxPhase: 'not_started',
  inboxFetched: 0,
  allMailFetched: 0,
  inboxStored: 0,
  allMailStored: 0,
  messagesTotal: null,
  messagesStored: 0,
  sendersCount: 0,
  lastHistoryId: null,
  startedAt: null,
  completedAt: null,
  activeJob: null,
  failedJob: null,
  accountMismatch: false,
  localAccount: null,
  ...over,
});
