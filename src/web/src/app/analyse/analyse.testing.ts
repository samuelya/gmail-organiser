import { computed, signal } from '@angular/core';
import { isActiveJob, JobDto, JobsConnectionState, JobStatus } from '../core/jobs.models';
import { ANALYSIS_RUN_JOB, AnalysisRunDto, GroupingPreviewDto } from './analysis.models';

/** Synthetic fixtures shared by the Analyse page specs. */
export const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: ANALYSIS_RUN_JOB,
  queue: 'analysis',
  status,
  progress: { done: 1, total: 4, message: 'Group 1 of 4' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

export const run = (over: Partial<AnalysisRunDto> = {}): AnalysisRunDto => ({
  id: 'run-1',
  jobId: 'job-1',
  kind: 'analyse',
  scope: 'inbox',
  senderAddress: null,
  requestedCount: 20,
  groupingMode: 'auto',
  status: 'running',
  messagesCovered: 0,
  messagesLlm: 0,
  messagesDerived: 0,
  messagesFromMemory: 0,
  llmCalls: 0,
  groups: 0,
  mixedGroups: 0,
  failedMessages: 0,
  skippedMessages: 0,
  model: null,
  promptVersion: null,
  error: null,
  savedPercent: 0,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  finishedAt: null,
  promptTokens: 0,
  completionTokens: 0,
  llmSeconds: 0,
  nearContextLimit: 0,
  policiesProposed: 0,
  triageCalls: 0,
  escalatedCalls: 0,
  isStalled: false,
  packedMessages: 0,
  packRetries: 0,
  ...over,
});

export const preview = (over: Partial<GroupingPreviewDto> = {}): GroupingPreviewDto => ({
  messages: 20,
  skipped: 0,
  groups: 5,
  estimatedLlmCalls: 5,
  estimatedDerived: 12,
  estimatedFromMemory: 0,
  embeddingsAvailable: true,
  largestGroups: [
    {
      key: 'g1',
      senderAddress: 'news@example.com',
      display: 'Weekly news',
      size: 8,
      representatives: 2,
    },
  ],
  ...over,
});

export class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

export const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
