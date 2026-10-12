import { vi } from 'vitest';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatTooltip } from '@angular/material/tooltip';
import { By } from '@angular/platform-browser';
import { provideLocationMocks } from '@angular/common/testing';
import { provideRouter, Router } from '@angular/router';
import { of, Subject } from 'rxjs';
import { PoliciesService } from '../policies/policies.service';
import { AnalysisService } from '../analyse/analysis.service';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { isActiveJob, JobDto, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { SuggestionAlternativeDto } from './alternative.models';
import { ReviewPage } from './review-page.component';
import {
  REVIEW_EDIT_DIALOG,
  ReviewGroupDto,
  ReviewSenderDetailDto,
  ReviewSenderDto,
  SenderPatternDto,
  SuggestionDto,
} from './review.models';
import { ReviewService } from './review.service';

/** Synthetic fixtures and the TestBed set-up shared by the Review page specs. */
export const sender = (over: Partial<ReviewSenderDto> = {}): ReviewSenderDto => ({
  address: 'news@example.com',
  displayName: 'Example News',
  pending: 3,
  approved: 2,
  rejected: 0,
  applied: 0,
  totalMessages: 10,
  policyId: null,
  ...over,
});

export const member = (id: string, over: Partial<SuggestionDto> = {}): SuggestionDto => ({
  id,
  messageId: `m-${id}`,
  subject: `Subject ${id}`,
  date: '2026-01-01T00:00:00Z',
  snippet: 'A synthetic preview',
  source: 'llm',
  topicLabel: 'Topic/Alpha',
  isNewLabel: true,
  needsAction: false,
  toBeDeleted: true,
  unsubscribeSuggested: false,
  confidence: 0.9,
  reason: 'Synthetic reason',
  status: 'pending',
  edited: false,
  protected: false,
  replaceLabels: [],
  currentLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

export const group = (over: Partial<ReviewGroupDto> = {}): ReviewGroupDto => ({
  groupKey: 'key-1',
  display: 'Weekly digest',
  size: 3,
  llmCount: 1,
  derivedCount: 2,
  memoryCount: 0,
  topicLabel: 'Topic/Alpha',
  needsAction: false,
  toBeDeleted: true,
  mixed: true,
  confidenceMin: 0.7,
  confidenceMax: 0.9,
  reason: 'Synthetic reason',
  members: [member('a'), member('b', { source: 'derived', protected: true })],
  truncated: false,
  replaceLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

export const detail = (groups = [group()]): ReviewSenderDetailDto => ({
  sender: sender(),
  groups,
  page: 1,
  pageSize: 20,
  totalGroups: groups.length,
});

export const noPattern: SenderPatternDto = {
  topicLabel: null,
  needsAction: null,
  toBeDeleted: null,
  approvals: 0,
  agreement: 0,
  remaining: 4,
  documentTypeLabel: null,
};

export const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: 'apply_actions',
  queue: 'gmail',
  status,
  progress: { done: 2, total: 4, message: 'Applied 2 of 4 messages' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

export const alternative = (
  over: Partial<SuggestionAlternativeDto> = {},
): SuggestionAlternativeDto => ({
  topicLabel: 'Topic/Beta',
  documentTypeLabel: null,
  replaceLabels: [],
  labelChange: 'add',
  needsAction: false,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.8,
  reason: 'Synthetic new reason',
  promptVersion: 'v9',
  model: null,
  createdAt: '2026-01-02T00:00:00Z',
  mixed: false,
  count: 1,
  ...over,
});

export const claudeItem = (over: Partial<ExternalReviewDto> = {}): ExternalReviewDto => ({
  id: 'r1',
  targetType: 'suggestion',
  suggestionId: 'b',
  senderAddress: 'news@example.com',
  groupKey: null,
  groupDisplay: null,
  status: 'running',
  reviewer: null,
  verdict: null,
  verdictTopicLabel: null,
  verdictNeedsAction: null,
  verdictToBeDeleted: null,
  verdictDocumentTypeLabel: null,
  verdictDocumentTypeSet: false,
  reasoning: null,
  error: null,
  resolution: 'none',
  createdAt: '2026-01-01T00:00:00Z',
  reviewedAt: null,
  resolvedAt: null,
  ...over,
});

export class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
  readonly externalReviewChanges = new Subject<ExternalReviewDto>();
  cancel = vi.fn(() => of(undefined));
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

export async function renderReviewPage(
  pattern: SenderPatternDto = noPattern,
  claudeReviewerMode = 'off',
  groups = [group()],
  alternatives = 0,
  url = '/',
  approved = 0,
) {
  const jobs = new FakeJobs();
  const firstPage = [sender(), sender({ address: 'shop@example.com', displayName: null })];
  const claude = {
    createReviews: vi.fn(() => of({ created: 1, skipped: 0, items: [] })),
  };
  const api = {
    // The first page lists two senders; a search also finds `other@example.com`, listed off that page.
    listSenders: vi.fn((_status: string, search: string) => {
      const items = search
        ? [...firstPage, sender({ address: 'other@example.com' })].filter((s) =>
            s.address.includes(search.toLowerCase()),
          )
        : firstPage;
      return of({ items, page: 1, pageSize: 25, total: items.length });
    }),
    sender: vi.fn(() => of(detail(groups))),
    pattern: vi.fn(() => of(pattern)),
    approve: vi.fn(() => of(member('a'))),
    reject: vi.fn(() => of(member('a'))),
    approveGroup: vi.fn(() => of({ changed: 1, skipped: ['b'] })),
    rejectGroup: vi.fn(() => of({ changed: 2, skipped: [] })),
    analyseIndividually: vi.fn(() => of({ id: 'run-1' })),
    apply: vi.fn(() => of({ id: 'batch-1', jobId: 'job-1' })),
    applyRest: vi.fn(() =>
      of({
        created: 4,
        protectedAdjusted: 1,
        batch: { id: 'batch-2', jobId: 'job-2' },
        filterCandidate: { from: 'news@example.com', listId: null },
      }),
    ),
    job: vi.fn(() => of(job('completed', { version: 5 }))),
    acceptAlternatives: vi.fn(() => of({ accepted: 1, discarded: 0, skipped: 0 })),
    discardAlternatives: vi.fn(() => of({ accepted: 0, discarded: 2, skipped: 0 })),
  };
  const analysis = {
    summary: vi.fn(() => of({ alternatives, approved })),
    startCompareRun: vi.fn(() => of({ id: 'run-2' })),
  };
  const edit = { editGroup: vi.fn(() => of(true)), editMember: vi.fn(() => of(false)) };
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideLocationMocks(),
      { provide: PoliciesService, useValue: { list: () => of({ items: [], total: 0 }) } },
      { provide: JobsService, useValue: jobs },
      { provide: ReviewService, useValue: api },
      { provide: AnalysisService, useValue: analysis },
      { provide: REVIEW_EDIT_DIALOG, useValue: edit },
      { provide: ClaudeService, useValue: claude },
      {
        provide: SettingsService,
        useValue: {
          getSettings: () =>
            of({
              bulkApproveThreshold: 0.85,
              actionLabelName: 'Act',
              deleteLabelName: 'Bin',
              claudeReviewerMode,
            }),
        },
      },
      { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
    ],
  });
  const router = TestBed.inject(Router);
  // Back and Forward reach the router as in the app.
  router.setUpLocationChangeListener();
  await router.navigateByUrl(url);
  const fixture = TestBed.createComponent(ReviewPage);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
  const settle = async () => {
    fixture.detectChanges();
    await fixture.whenStable();
  };
  const expand = async () => {
    q('group-expand')!.click();
    await settle();
  };
  const tooltip = (id: string) =>
    fixture.debugElement.query(By.css(`[data-testid="${id}"]`)).injector.get(MatTooltip).message;
  const tick = async (...indexes: number[]) => {
    const boxes = el.querySelectorAll<HTMLInputElement>('[data-testid="member-select"] input');
    indexes.forEach((i) => boxes[i].click());
    await settle();
  };
  return {
    fixture,
    jobs,
    api,
    analysis,
    edit,
    claude,
    el,
    q,
    all,
    settle,
    expand,
    tooltip,
    tick,
  };
}
