import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatTooltip } from '@angular/material/tooltip';
import { By } from '@angular/platform-browser';
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

const sender = (over: Partial<ReviewSenderDto> = {}): ReviewSenderDto => ({
  address: 'news@example.com',
  displayName: 'Example News',
  pending: 3,
  approved: 2,
  rejected: 0,
  applied: 0,
  totalMessages: 10,
  ...over,
});

const member = (id: string, over: Partial<SuggestionDto> = {}): SuggestionDto => ({
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

const group = (over: Partial<ReviewGroupDto> = {}): ReviewGroupDto => ({
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

const detail = (groups = [group()]): ReviewSenderDetailDto => ({
  sender: sender(),
  groups,
  page: 1,
  pageSize: 20,
  totalGroups: groups.length,
});

const noPattern: SenderPatternDto = {
  topicLabel: null,
  needsAction: null,
  toBeDeleted: null,
  approvals: 0,
  agreement: 0,
  remaining: 4,
  documentTypeLabel: null,
};

const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
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

class FakeJobs {
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

describe('ReviewPage', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(
    pattern: SenderPatternDto = noPattern,
    claudeReviewerMode = 'off',
    groups = [group()],
    alternatives = 0,
  ) {
    const jobs = new FakeJobs();
    const claude = {
      createReviews: vi.fn(() => of({ created: 1, skipped: 0, items: [] })),
    };
    const api = {
      listSenders: vi.fn(() =>
        of({
          items: [sender(), sender({ address: 'shop@example.com', displayName: null })],
          page: 1,
          pageSize: 25,
          total: 2,
        }),
      ),
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
      summary: vi.fn(() => of({ alternatives })),
      startCompareRun: vi.fn(() => of({ id: 'run-2' })),
    };
    const edit = { editGroup: vi.fn(() => of(true)), editMember: vi.fn(() => of(false)) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
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

  const dialogButton = (testId: string) =>
    document.querySelector<HTMLButtonElement>(`mat-dialog-container [data-testid="${testId}"]`)!;
  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';

  it('lists senders, selects the first and renders its groups', async () => {
    const { api, q, all } = await render();
    expect(api.listSenders).toHaveBeenCalledWith('pending', '', 1, 25, false);
    expect(all('sender-item')).toHaveLength(2);
    expect(all('sender-count')[0].textContent).toContain('3');
    expect(api.sender).toHaveBeenCalledWith('news@example.com', 'pending', 1, 20, false);
    expect(q('detail-title')!.textContent).toContain('Example News');
    expect(q('group-title')!.textContent).toContain('Weekly digest');
    expect(q('group-origin')!.textContent).toContain('1 analysed by the model · 2 derived');
    expect(q('group-new-label')).not.toBeNull();
    expect(q('group-delete')!.textContent).toContain('Bin');
    expect(q('group-confidence')!.textContent).toContain('70%–90%');
    expect(q('group-mixed')).not.toBeNull();
    expect(q('apply-approved')!.textContent).toContain('Apply approved (2)');
    expect(q('apply-rest')).toBeNull();
  });

  it('re-fetches the detail for another sender and status', async () => {
    const { api, all, el, settle } = await render();
    all('sender-item')[1].click();
    await settle();
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'pending', 1, 20, false);
    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[1].click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('approved', '', 1, 25, false);
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'approved', 1, 20, false);
  });

  it('approves and rejects a member, then re-fetches', async () => {
    const { api, all, expand, settle } = await render();
    await expand();
    expect(all('member-row')).toHaveLength(2);
    expect(all('member-source')[1].textContent).toContain('derived');
    expect(all('member-protected')).toHaveLength(1);
    const calls = api.sender.mock.calls.length;
    all('member-approve')[0].click();
    await settle();
    expect(api.approve).toHaveBeenCalledWith('a');
    all('member-reject')[1].click();
    await settle();
    expect(api.reject).toHaveBeenCalledWith('b');
    expect(api.sender.mock.calls.length).toBe(calls + 2);
  });

  it("approves a group with the card's outcome and shows the skipped members", async () => {
    const { api, q, all, expand, settle } = await render(noPattern, 'off', [
      group({ documentTypeLabel: 'Docs/Invoice' }),
    ]);
    q('group-approve')!.click();
    await settle();
    expect(api.approveGroup).toHaveBeenCalledWith('news@example.com', 'key-1', {
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: true,
      documentTypeLabel: 'Docs/Invoice',
    });
    expect(snackText()).toContain('1 member was skipped');
    await expand();
    expect(all('member-skipped')).toHaveLength(1);
    q('group-reject')!.click();
    await settle();
    expect(api.rejectGroup).toHaveBeenCalledWith('news@example.com', 'key-1');
  });

  it('decides a message analysed on its own by its suggestion', async () => {
    const { api, q, settle } = await render();
    api.sender.mockReturnValue(
      of(detail([group({ groupKey: null, size: 1, members: [member('solo')] })])),
    );
    q('group-reject')!.click();
    await settle();
    q('group-approve')!.click();
    await settle();
    expect(api.approve).toHaveBeenCalledWith('solo');
    expect(api.approveGroup).not.toHaveBeenCalled();
  });

  it('opens the edit hook for a group and re-fetches when saved', async () => {
    const { api, edit, q, settle } = await render();
    const calls = api.sender.mock.calls.length;
    q('group-edit')!.click();
    await settle();
    expect(edit.editGroup).toHaveBeenCalledWith(
      'news@example.com',
      expect.objectContaining({ groupKey: 'key-1' }),
    );
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  it('analyses the selected members individually', async () => {
    const { api, q, el, expand, settle } = await render();
    await expand();
    el.querySelectorAll<HTMLInputElement>('[data-testid="member-select"] input').forEach((i) =>
      i.click(),
    );
    await settle();
    expect(q('analyse-individually')!.textContent).toContain('(2)');
    q('analyse-individually')!.click();
    await settle();
    expect(api.analyseIndividually).toHaveBeenCalledWith(['a', 'b']);
    expect(snackText()).toContain('Analysing 2 messages individually');
    expect(q('analyse-individually')!.textContent).toContain('(0)');
  });

  it('applies approved and follows the job from the hub until it completes', async () => {
    const { api, jobs, q, settle } = await render();
    q('apply-approved')!.click();
    await settle();
    expect(api.apply).toHaveBeenCalledWith('news@example.com');
    expect(q('apply-progress')).not.toBeNull();
    jobs.held.set([job('running')]);
    await settle();
    expect(q('apply-message')!.textContent).toContain('Applied 2 of 4 messages');
    const calls = api.sender.mock.calls.length;
    jobs.held.set([
      job('completed', {
        version: 2,
        progress: {
          done: 3,
          total: 4,
          message: 'Applied 3 of 4 messages; 1 skipped (label refused)',
        },
      }),
    ]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    expect(snackText()).toContain('1 skipped (label refused)');
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  it('reads the apply job from the API after a reconnect', async () => {
    const { api, jobs, q, settle } = await render();
    q('apply-approved')!.click();
    await settle();
    jobs.reconnects.set(1);
    await settle();
    expect(api.job).toHaveBeenCalledWith('job-1');
    expect(q('apply-progress')).toBeNull();
  });

  it('picks up an apply job already running on load and blocks a second apply', async () => {
    const { api, jobs, q, settle } = await render();
    jobs.held.set([job('running', { id: 'job-9', type: 'mailbox_fetch' })]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    jobs.held.set([job('paused', { id: 'job-7' })]);
    await settle();
    expect(q('apply-message')!.textContent).toContain('Applied 2 of 4 messages');
    expect((q('apply-approved') as HTMLButtonElement).disabled).toBe(true);
    q('apply-approved')!.click();
    await settle();
    expect(api.apply).not.toHaveBeenCalled();
    q('apply-cancel')!.click();
    await settle();
    dialogButton('confirm-ok').click();
    await settle();
    expect(jobs.cancel).toHaveBeenCalledWith('job-7');
    jobs.held.set([job('cancelled', { id: 'job-7', version: 2 })]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    expect((q('apply-approved') as HTMLButtonElement).disabled).toBe(false);
  });

  it('picks up an apply job from the snapshot after a reconnect', async () => {
    const { jobs, q, settle } = await render();
    jobs.reconnects.set(1);
    jobs.held.set([job('queued', { id: 'job-8' })]);
    await settle();
    expect(q('apply-progress')).not.toBeNull();
  });

  it('applies to the rest of the sender after confirming the pattern', async () => {
    const { api, jobs, q, settle } = await render({
      topicLabel: 'Topic/Alpha',
      needsAction: true,
      toBeDeleted: false,
      approvals: 4,
      agreement: 0.75,
      remaining: 6,
      documentTypeLabel: null,
    });
    expect(q('apply-rest')!.textContent).toContain('remaining 6');
    q('apply-rest')!.click();
    await settle();
    const dialog = document.querySelector('mat-dialog-container')!.textContent!;
    expect(dialog).toContain('label "Topic/Alpha", "Act"');
    expect(dialog).toContain('A Gmail filter for this sender can be created afterwards');
    dialogButton('confirm-ok').click();
    await settle();
    await settle();
    expect(api.applyRest).toHaveBeenCalledWith('news@example.com', {});
    expect(q('apply-progress')).not.toBeNull();
    expect(snackText()).toContain('Created 4 suggestions');
    // The apply job's completion snackbar replaces this one, so it carries "Create filter".
    jobs.held.set([job('completed', { id: 'job-2', version: 2 })]);
    await settle();
    await settle();
    expect(snackText()).toContain('Applied 2 of 4 messages');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const action = document.querySelector<HTMLButtonElement>('mat-snack-bar-container button')!;
    expect(action.textContent).toContain('Create filter');
    action.click();
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/rules'], {
      queryParams: { propose: 'news@example.com' },
    });
  });

  it("names the pattern's document type without a parent in settings and does not re-send it", async () => {
    const { api, q, settle } = await render({
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: false,
      approvals: 4,
      agreement: 0.75,
      remaining: 6,
      documentTypeLabel: 'Docs/Invoice',
    });
    q('apply-rest')!.click();
    await settle();
    const dialog = document.querySelector('mat-dialog-container')!.textContent!;
    expect(dialog).toContain('label "Topic/Alpha", document type "Docs/Invoice"');
    dialogButton('confirm-ok').click();
    await settle();
    expect(api.applyRest).toHaveBeenCalledWith('news@example.com', {});
  });

  it('does not apply to the rest when the confirm is cancelled', async () => {
    const { api, q, settle } = await render({
      ...noPattern,
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: false,
    });
    q('apply-rest')!.click();
    await settle();
    dialogButton('confirm-cancel').click();
    await settle();
    expect(api.applyRest).not.toHaveBeenCalled();
  });

  const claudeItem = (over: Partial<ExternalReviewDto> = {}): ExternalReviewDto => ({
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

  it('hides every Claude action and hint when Claude review is off', async () => {
    const { q, expand } = await render(noPattern, 'off', [
      group({ suggestedForClaude: true, members: [member('a', { suggestedForClaude: true })] }),
    ]);
    await expand();
    expect(q('claude-panel')).toBeNull();
    expect(q('claude-send-pending')).toBeNull();
    expect(q('group-claude-hint')).toBeNull();
    expect(q('member-claude-hint')).toBeNull();
  });

  it('shows the Claude? hint only on items suggested for Claude', async () => {
    const { q, all, expand } = await render(noPattern, 'headless_claude_code', [
      group({
        suggestedForClaude: true,
        members: [member('a', { suggestedForClaude: true }), member('b')],
      }),
    ]);
    await expand();
    expect(q('group-claude-hint')!.textContent).toContain('Claude?');
    expect(all('member-claude-hint')).toHaveLength(1);
  });

  it('sends a group card and a member row to Claude', async () => {
    const { claude, all, expand } = await render(noPattern, 'headless_claude_code');
    await expand();
    const sends = all('claude-send');
    expect(sends).toHaveLength(3);
    sends[0].click();
    expect(claude.createReviews).toHaveBeenLastCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1' }],
    });
    sends[2].click();
    expect(claude.createReviews).toHaveBeenLastCalledWith({ suggestionIds: ['b'] });
  });

  it('a hub change patches the matching row in place', async () => {
    const { jobs, api, el, settle, expand } = await render(noPattern, 'headless_claude_code');
    await expand();
    const calls = api.sender.mock.calls.length;
    jobs.externalReviewChanges.next(claudeItem());
    jobs.externalReviewChanges.next(
      claudeItem({
        id: 'r2',
        targetType: 'group',
        suggestionId: null,
        groupKey: 'key-1',
        status: 'queued',
      }),
    );
    await settle();
    const rows = el.querySelectorAll('[data-testid="member-row"]');
    expect(rows[0].querySelector('[data-testid="claude-running"]')).toBeNull();
    expect(rows[1].querySelector('[data-testid="claude-running"]')).not.toBeNull();
    expect(el.querySelectorAll('[data-testid="claude-queued"]')).toHaveLength(1);
    expect(api.sender.mock.calls.length).toBe(calls);
  });

  it('re-fetches once an item is accepted, since its suggestions changed', async () => {
    const { jobs, api, settle } = await render(noPattern, 'headless_claude_code');
    const calls = api.sender.mock.calls.length;
    jobs.externalReviewChanges.next(
      claudeItem({ status: 'reviewed', verdict: 'agree', resolution: 'accepted_claude' }),
    );
    await settle();
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  const alternative = (over: Partial<SuggestionAlternativeDto> = {}): SuggestionAlternativeDto => ({
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

  it('re-analyses the ticked members after confirming, then clears the selection', async () => {
    const { analysis, q, expand, tick, settle } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b', { status: 'applied' })] }),
    ]);
    await expand();
    await tick(0, 1);
    expect(q('reanalyse-selection')!.textContent).toContain('Re-analyse (2)');
    q('reanalyse-selection')!.click();
    await settle();
    const text = document.querySelector('mat-dialog-container')!.textContent!;
    expect(text).toContain('Re-analyse 2 emails?');
    expect(text).toContain('memory shortcut is skipped');
    dialogButton('confirm-ok').click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['a', 'b'] });
    expect(snackText()).toContain('Re-analysing 2 messages again');
    await settle();
    expect(q('reanalyse-selection')!.textContent).toContain('(0)');
  });

  it('both selection buttons are disabled while a re-analyse is starting', async () => {
    const { q, expand, tick, settle } = await render();
    await expand();
    await tick(0);
    q('reanalyse-selection')!.click();
    await settle();
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).toBe('true');
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).toBe('true');
    dialogButton('confirm-cancel').click();
    await settle();
    await settle();
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).not.toBe('true');
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('an applied member can be ticked; Analyse individually then explains it is blocked', async () => {
    const { api, q, expand, tick, tooltip } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b', { status: 'applied' })] }),
    ]);
    await expand();
    await tick(1);
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('analyse-individually')).toBe('Applied emails can only be re-analysed');
    q('analyse-individually')!.click();
    expect(api.analyseIndividually).not.toHaveBeenCalled();
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('a reload drops ticks on members no longer shown, so an applied one cannot stay ticked', async () => {
    const { api, q, all, expand, tick, settle } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b')] }),
    ]);
    await expand();
    await tick(0, 1);
    expect(q('analyse-individually')!.textContent).toContain('(2)');
    // "b" was applied meanwhile: the pending tab no longer lists it.
    api.sender.mockReturnValue(of(detail([group({ members: [member('a')] })])));
    all('member-approve')[0].click();
    await settle();
    expect(q('analyse-individually')!.textContent).toContain('(1)');
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('Re-analyse is disabled with a tooltip while an analysis run is queued or running', async () => {
    const { jobs, analysis, q, expand, tick, settle, tooltip } = await render();
    await expand();
    await tick(0);
    jobs.held.set([job('queued', { id: 'job-a', type: 'analysis_run' })]);
    await settle();
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('reanalyse-selection')).toBe('An analysis is running');
    q('reanalyse-selection')!.click();
    await settle();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
  });

  it('filters by Re-analysed with the count from the summary', async () => {
    const { api, el, q, settle } = await render(noPattern, 'off', [group()], 4);
    expect(q('filter-reanalysed')!.textContent).toContain('Re-analysed (4)');
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 1, 25, true);
    expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, true);
  });

  it('hides the Re-analysed filter while nothing waits', async () => {
    const { q } = await render();
    expect(q('filter-reanalysed')).toBeNull();
  });

  it('Use new on an approved member notes it is pending again; Keep current on a card names the group and tab', async () => {
    const alt = alternative();
    const { api, q, all, expand, settle } = await render(noPattern, 'off', [
      group({
        alternative: alternative({ count: 1 }),
        members: [member('a', { status: 'approved', alternative: alt }), member('b')],
      }),
    ]);
    await expand();
    const compares = all('alternative');
    expect(compares).toHaveLength(2);
    compares[1].querySelector<HTMLButtonElement>('[data-testid="alternative-use"]')!.click();
    await settle();
    expect(api.acceptAlternatives).toHaveBeenCalledWith({ suggestionIds: ['a'] });
    expect(snackText()).toContain('Used the new result for 1 suggestion.');
    expect(q('pending-again')!.textContent).toContain(
      'pending again: approve it and apply it to change Gmail',
    );

    compares[0].querySelector<HTMLButtonElement>('[data-testid="alternative-keep"]')!.click();
    await settle();
    expect(api.discardAlternatives).toHaveBeenCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1', status: 'pending' }],
    });
  });

  it('Use new on a pending member adds no note', async () => {
    const { q, all, expand, settle } = await render(noPattern, 'off', [
      group({ members: [member('a', { alternative: alternative() }), member('b')] }),
    ]);
    await expand();
    all('alternative-use')[0].click();
    await settle();
    expect(q('pending-again')).toBeNull();
  });

  it('the Applied tab is read-only except ticks, Re-analyse and Use new / Keep current', async () => {
    const applied = (id: string) => member(id, { status: 'applied', alternative: alternative() });
    const { api, el, q, all, expand, tick, settle } = await render(noPattern, 'off', [
      group({ alternative: alternative({ count: 2 }), members: [applied('a'), applied('b')] }),
    ]);
    const tabs = el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button');
    expect([...tabs].map((t) => t.textContent?.trim())).toContain('Applied');
    tabs[3].click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('applied', '', 1, 25, false);
    expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'applied', 1, 20, false);
    await expand();
    for (const id of ['group-approve', 'group-reject', 'group-edit']) expect(q(id)).toBeNull();
    for (const id of ['member-approve', 'member-reject', 'member-edit'])
      expect(all(id)).toHaveLength(0);
    await tick(0);
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
    all('alternative-use')[0].click();
    await settle();
    expect(api.acceptAlternatives).toHaveBeenCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1', status: 'applied' }],
    });
    expect(q('pending-again')).not.toBeNull();
  });

  it('a tab or Re-analysed change selects the first listed sender when the selected one is not listed', async () => {
    const { api, el, settle } = await render(noPattern, 'off', [group()], 4);
    const only = (address: string) =>
      of({ items: [sender({ address })], page: 1, pageSize: 25, total: 1 });
    // The fifth argument is the Re-analysed filter.
    api.listSenders.mockImplementation((...args: unknown[]) =>
      only(args[4] ? 'shop@example.com' : 'news@example.com'),
    );
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'pending', 1, 20, true);
    api.listSenders.mockImplementation(() => of({ items: [], page: 1, pageSize: 25, total: 0 }));
    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[1].click();
    await settle();
    expect(el.querySelector('[data-testid="detail-none"]')).not.toBeNull();
  });

  it('a Re-analysed change never requests the detail of a sender the new list hides', async () => {
    const { api, el, settle } = await render(noPattern, 'off', [group()], 4);
    const filtered = new Subject<{
      items: ReviewSenderDto[];
      page: number;
      pageSize: number;
      total: number;
    }>();
    api.listSenders.mockImplementation((...args: unknown[]) =>
      args[4] ? filtered : of({ items: [sender()], page: 1, pageSize: 25, total: 1 }),
    );
    api.sender.mockClear();
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.sender).not.toHaveBeenCalled();
    filtered.next({
      items: [sender({ address: 'shop@example.com' })],
      page: 1,
      pageSize: 25,
      total: 1,
    });
    await settle();
    expect(api.sender).toHaveBeenCalledTimes(1);
    expect(api.sender).toHaveBeenCalledWith('shop@example.com', 'pending', 1, 20, true);
  });
});
