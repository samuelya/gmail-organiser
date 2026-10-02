import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState, JobStatus } from '../core/jobs.models';
import { AnalysisSummaryDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { JobsService } from '../core/jobs.service';
import { DashboardPage, STATUS_REFRESH_MS } from './dashboard-page.component';
import { FetchStatusDto, fetchJobsKey, fetchView, humanise } from './fetch.models';
import { FetchService } from './fetch.service';

const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
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

const status = (over: Partial<FetchStatusDto> = {}): FetchStatusDto => ({
  accountEmail: 'user@example.com',
  mailboxPhase: 'not_started',
  inboxFetched: 0,
  allMailFetched: 0,
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

const analysisSummary: AnalysisSummaryDto = {
  notAnalysed: 0,
  analysed: 0,
  approved: 0,
  rejected: 0,
  applied: 0,
  actionCount: 0,
  toBeDeletedCount: 0,
  totalLlmCalls: 0,
  totalMessagesCovered: 0,
  savedPercent: 0,
};

/** Signals and calls the page reads from `JobsService`, driven by the test. */
class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  pause = vi.fn(() => of(undefined));
  resume = vi.fn(() => of(undefined));
  cancel = vi.fn(() => of(undefined));
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

describe('fetchView', () => {
  it.each([
    ['not_started', 'Not started', 'Start fetch'],
    ['inbox', 'Inbox', 'Start fetch'],
    ['all_mail', 'All mail', 'Start fetch'],
    ['completed', 'Completed', 'Fetch new mail'],
  ] as const)('%s with no job: phase %s, Start labelled %s', (phase, label, start) => {
    const v = fetchView(status({ mailboxPhase: phase }), undefined);
    expect(v.phase).toBe(label);
    expect(v.startLabel).toBe(start);
    expect(v.controls).toEqual({ pause: false, resume: false, cancel: false });
  });

  it.each([
    ['queued', { pause: true, resume: false, cancel: true }],
    ['running', { pause: true, resume: false, cancel: true }],
    ['paused', { pause: false, resume: true, cancel: true }],
  ] as const)('an active %s job hides Start and allows %o', (jobStatus, controls) => {
    const v = fetchView(status({ mailboxPhase: 'inbox', activeJob: job(jobStatus) }), undefined);
    expect(v.startLabel).toBeNull();
    expect(v.controls).toEqual(controls);
  });

  it('an unknown phase and job type are humanised, not dropped', () => {
    const active = job('running', { type: 'incremental_fetch' });
    const v = fetchView(status({ mailboxPhase: 'reconcile', activeJob: active }), undefined);
    expect(v.phase).toBe('Reconcile');
    expect(v.job!.type).toBe('incremental_fetch');
    expect(humanise('incremental_fetch')).toBe('Incremental fetch');
  });

  it('a failed job offers Resume fetch through Start and shows its error', () => {
    const failed = job('failed', { error: 'Gmail quota exceeded' });
    const v = fetchView(status({ mailboxPhase: 'all_mail', failedJob: failed }), undefined);
    expect(v.startLabel).toBe('Resume fetch');
    expect(v.error).toBe('Gmail quota exceeded');
  });

  it('shows whichever job the status names, using the newer hub copy', () => {
    const active = job('running', { type: 'incremental_fetch', version: 3 });
    const live = job('paused', { type: 'incremental_fetch', version: 4 });
    expect(fetchView(status({ activeJob: active }), live).job).toBe(live);
    expect(fetchView(status({ activeJob: live }), active).job).toBe(live);
  });

  it('an account mismatch disables Start and Resume', () => {
    const v = fetchView(status({ accountMismatch: true, activeJob: job('paused') }), undefined);
    expect(v.controls.resume).toBe(false);
    expect(fetchView(status({ accountMismatch: true }), undefined).startDisabled).toBe(true);
  });

  it('fetchJobsKey ignores progress and other queues', () => {
    const a = fetchJobsKey([job('running'), job('running', { id: 'x', queue: 'analysis' })]);
    const b = fetchJobsKey([job('running', { progress: { done: 99, total: 100, message: null } })]);
    expect(a).toBe(b);
    expect(fetchJobsKey([job('paused')])).not.toBe(a);
    const nextStep = { done: 0, total: 100, message: 'Next step' };
    expect(fetchJobsKey([job('running', { progress: nextStep })])).not.toBe(a);
  });
});

describe('DashboardPage', () => {
  let jobs: FakeJobs;
  let fetch: { getStatus: ReturnType<typeof vi.fn>; startMailboxFetch: ReturnType<typeof vi.fn> };

  async function render(initial: FetchStatusDto) {
    jobs = new FakeJobs();
    fetch = {
      getStatus: vi.fn(() => of(initial)),
      startMailboxFetch: vi.fn(() => of({ jobId: 'job-1' })),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: jobs },
        { provide: FetchService, useValue: fetch },
        { provide: AnalysisService, useValue: { summary: () => of(analysisSummary) } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(DashboardPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('not started: phase, counts and Start; Start posts and reloads the status', async () => {
    const { fixture, q } = await render(status({ inboxFetched: 3, sendersCount: 2 }));
    expect(q('phase')!.textContent).toContain('Not started');
    expect(q('inbox-fetched')!.textContent).toContain('3');
    expect(q('senders-count')!.textContent).toContain('2');
    expect(q('fetch-progress')).toBeNull();
    expect(q('pause')).toBeNull();

    fetch.getStatus.mockReturnValue(
      of(status({ mailboxPhase: 'inbox', activeJob: job('queued') })),
    );
    q('start')!.click();
    await fixture.whenStable();
    expect(fetch.startMailboxFetch).toHaveBeenCalledTimes(1);
    expect(q('start')).toBeNull();
    expect(q('pause')).not.toBeNull();
  });

  it('running: progress from the live job, Pause and Cancel', async () => {
    const { fixture, q } = await render(
      status({ mailboxPhase: 'inbox', activeJob: job('running') }),
    );
    jobs.held.set([
      job('running', { version: 2, progress: { done: 60, total: 120, message: null } }),
    ]);
    await fixture.whenStable();
    expect(q('fetch-progress-text')!.textContent).toContain('60 of 120 · 50%');
    expect(q('resume')).toBeNull();

    q('pause')!.click();
    await fixture.whenStable();
    expect(jobs.pause).toHaveBeenCalledWith('job-1');
  });

  it('Inbox and All mail show fetched of total with a percentage', async () => {
    const { q } = await render(
      status({ inboxFetched: 3200, inboxTotal: 12450, allMailFetched: 7, allMailTotal: 7 }),
    );
    expect(q('inbox-fetched')!.textContent!.trim()).toBe('3,200 of 12,450 · 25%');
    expect(q('all-mail-fetched')!.textContent!.trim()).toBe('7 of 7 · 100%');
  });

  it('null or missing totals show the count only', async () => {
    const { q } = await render(status({ inboxFetched: 3200, inboxTotal: null }));
    expect(q('inbox-fetched')!.textContent!.trim()).toBe('3,200');
    expect(q('all-mail-fetched')!.textContent!.trim()).toBe('0');
  });

  it('the Running jobs row uses the same done of total formatter', async () => {
    const { fixture, q } = await render(status());
    jobs.held.set([job('running', { progress: { done: 999, total: 1000, message: null } })]);
    await fixture.whenStable();
    expect(q('job-progress-text')!.textContent!.trim()).toBe('999 of 1,000 · 99%');
  });

  it('progress is indeterminate while the total is unknown', async () => {
    const running = job('running', { progress: { done: 5, total: null, message: null } });
    const { q } = await render(status({ mailboxPhase: 'inbox', activeJob: running }));
    expect(q('fetch-progress')!.querySelector('mat-progress-bar')!.getAttribute('mode')).toBe(
      'indeterminate',
    );
  });

  it('paused: Resume calls the job endpoint', async () => {
    const { fixture, q } = await render(
      status({ mailboxPhase: 'all_mail', activeJob: job('paused') }),
    );
    expect(q('pause')).toBeNull();
    q('resume')!.click();
    await fixture.whenStable();
    expect(jobs.resume).toHaveBeenCalledWith('job-1');
  });

  it('Cancel asks first', async () => {
    const { fixture, q } = await render(
      status({ mailboxPhase: 'inbox', activeJob: job('running') }),
    );
    q('cancel')!.click();
    await fixture.whenStable();
    document.querySelector<HTMLElement>('[data-testid="confirm-ok"]')!.click();
    await fixture.whenStable();
    expect(jobs.cancel).toHaveBeenCalledWith('job-1');
  });

  it('completed: Fetch new mail and the last completed time', async () => {
    const { q } = await render(
      status({ mailboxPhase: 'completed', completedAt: '2026-01-02T03:04:05Z', messagesStored: 7 }),
    );
    expect(q('start')!.textContent).toContain('Fetch new mail');
    expect(q('completed-at')!.textContent).toContain('2026');
    expect(q('messages-stored')!.textContent).toContain('7');
  });

  it('failed: shows the error as a warning and Resume fetch', async () => {
    const failed = job('failed', { error: 'Gmail quota exceeded' });
    const { q } = await render(status({ mailboxPhase: 'inbox', failedJob: failed }));
    expect(q('fetch-error')!.textContent).toContain('Gmail quota exceeded');
    expect(q('start')!.textContent).toContain('Resume fetch');
  });

  it('account mismatch: banner with the masked account and a Settings link; Start disabled', async () => {
    const { q } = await render(status({ accountMismatch: true, localAccount: 'u***@example.com' }));
    expect(q('local-account')!.textContent).toBe('u***@example.com');
    expect(q('mismatch-banner')!.querySelector('a')!.getAttribute('href')).toBe('/settings');
    expect((q('start') as HTMLButtonElement).disabled).toBe(true);
  });

  it('lists running jobs with their controls', async () => {
    const { fixture, el, q } = await render(status());
    expect(q('no-jobs')).not.toBeNull();
    jobs.held.set([job('running', { id: 'j1', queue: 'analysis', type: 'analysis_run' })]);
    await fixture.whenStable();
    expect(el.querySelectorAll('[data-testid="job-row"]').length).toBe(1);
    q('job-pause')!.click();
    expect(jobs.pause).toHaveBeenCalledWith('j1');
  });

  it('refetches the status on a fetch job status change and on reconnect, not on progress ticks', async () => {
    const { fixture } = await render(status({ activeJob: job('running') }));
    expect(fetch.getStatus).toHaveBeenCalledTimes(1);

    jobs.held.set([job('running', { version: 2 })]);
    await fixture.whenStable();
    jobs.held.set([
      job('running', { version: 3, progress: { done: 80, total: 100, message: null } }),
    ]);
    await fixture.whenStable();
    expect(fetch.getStatus).toHaveBeenCalledTimes(2); // the job appeared once

    jobs.held.set([job('completed', { version: 4 })]);
    await fixture.whenStable();
    expect(fetch.getStatus).toHaveBeenCalledTimes(3);

    jobs.reconnects.set(1);
    await fixture.whenStable();
    expect(fetch.getStatus).toHaveBeenCalledTimes(4);
  });

  it('account mismatch: the Running jobs card offers no Resume for a fetch job', async () => {
    const { fixture, el } = await render(
      status({ accountMismatch: true, mailboxPhase: 'inbox', activeJob: job('paused') }),
    );
    jobs.held.set([
      job('paused', { version: 2 }),
      job('paused', { id: 'j2', queue: 'analysis', type: 'analysis_run' }),
    ]);
    await fixture.whenStable();
    const rows = el.querySelectorAll('[data-testid="job-row"]');
    expect(rows[0].querySelector('[data-testid="job-resume"]')).toBeNull();
    expect(rows[1].querySelector('[data-testid="job-resume"]')).not.toBeNull();
  });

  it('refetches the counts at most every 5 s while a fetch job runs', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    try {
      const { fixture, q } = await render(status({ activeJob: job('running') }));
      jobs.held.set([job('running', { version: 2 })]);
      await fixture.whenStable();
      const calls = fetch.getStatus.mock.calls.length;

      fetch.getStatus.mockReturnValue(of(status({ activeJob: job('running'), inboxFetched: 40 })));
      for (let v = 3; v < 6; v++) {
        jobs.held.set([
          job('running', { version: v, progress: { done: v * 10, total: 100, message: null } }),
        ]);
        await fixture.whenStable();
      }
      expect(fetch.getStatus).toHaveBeenCalledTimes(calls);

      vi.advanceTimersByTime(STATUS_REFRESH_MS);
      await fixture.whenStable();
      expect(fetch.getStatus).toHaveBeenCalledTimes(calls + 1);
      expect(q('inbox-fetched')!.textContent).toContain('40');
    } finally {
      vi.useRealTimers();
    }
  });

  it('an older status response never overwrites a newer one', async () => {
    const { fixture, q } = await render(status());
    const first = new Subject<FetchStatusDto>();
    const second = new Subject<FetchStatusDto>();
    fetch.getStatus.mockReturnValueOnce(first).mockReturnValueOnce(second);
    fixture.componentInstance.load();
    fixture.componentInstance.load();

    second.next(status({ mailboxPhase: 'inbox', activeJob: job('running') }));
    first.next(status());
    await fixture.whenStable();
    expect(q('start')).toBeNull();
    expect(q('pause')).not.toBeNull();
  });

  it('busy holds until the status that follows the request, not an earlier refresh', async () => {
    const running = job('running');
    const { fixture, q } = await render(status({ mailboxPhase: 'inbox', activeJob: running }));
    const earlier = new Subject<FetchStatusDto>();
    const after = new Subject<FetchStatusDto>();
    fetch.getStatus.mockReturnValueOnce(earlier).mockReturnValueOnce(after);
    const pause = new Subject<undefined>();
    jobs.pause = vi.fn(() => pause);

    fixture.componentInstance.load();
    q('pause')!.click();
    earlier.next(status({ mailboxPhase: 'inbox', activeJob: running }));
    await fixture.whenStable();
    expect((q('cancel') as HTMLButtonElement).disabled).toBe(true);

    pause.next(undefined);
    await fixture.whenStable();
    expect((q('cancel') as HTMLButtonElement).disabled).toBe(true);

    after.next(status({ mailboxPhase: 'inbox', activeJob: running }));
    await fixture.whenStable();
    expect((q('cancel') as HTMLButtonElement).disabled).toBe(false);
  });

  it('a failed refetch shows the error and Retry next to the last status', async () => {
    const { fixture, q } = await render(status({ inboxFetched: 3 }));
    fetch.getStatus.mockReturnValue(throwError(() => new Error('offline')));
    fixture.componentInstance.load();
    await fixture.whenStable();
    expect(q('load-error')!.textContent).toContain('could not be refreshed');
    expect(q('inbox-fetched')!.textContent).toContain('3');

    fetch.getStatus.mockReturnValue(of(status({ inboxFetched: 4 })));
    q('load-error')!.querySelector('button')!.click();
    await fixture.whenStable();
    expect(q('load-error')).toBeNull();
    expect(q('inbox-fetched')!.textContent).toContain('4');
  });

  it('shows Pausing… until the job status changes and blocks a repeat click', async () => {
    const running = job('running');
    const { fixture, el, q } = await render(status({ mailboxPhase: 'inbox', activeJob: running }));
    jobs.held.set([running]);
    await fixture.whenStable();

    q('pause')!.click();
    await fixture.whenStable();
    expect(q('fetch-progress-text')!.textContent).toContain('Pausing…');
    expect(q('job-status')!.textContent).toContain('Pausing…');
    expect(q('fetch-pending-hint')!.textContent).toContain('Finishing the current batch…');
    expect(q('job-pending-hint')!.textContent).toContain('Finishing the current batch…');
    expect((q('pause') as HTMLButtonElement).disabled).toBe(true);
    expect((q('job-pause') as HTMLButtonElement).disabled).toBe(true);
    expect((q('cancel') as HTMLButtonElement).disabled).toBe(false);

    jobs.held.set([job('paused', { version: 2 })]);
    await fixture.whenStable();
    expect(q('fetch-progress-text')!.textContent).toContain('paused');
    expect(el.textContent).not.toContain('Pausing…');
    expect(el.textContent).not.toContain('Finishing the current batch');
  });

  it('shows Cancelling… after a confirmed cancel', async () => {
    const running = job('running', { id: 'j1', queue: 'analysis', type: 'analysis_run' });
    const { fixture, q } = await render(status());
    jobs.held.set([running]);
    await fixture.whenStable();
    q('job-cancel')!.click();
    await fixture.whenStable();
    document.querySelector<HTMLElement>('[data-testid="confirm-ok"]')!.click();
    await fixture.whenStable();
    expect(q('job-status')!.textContent).toContain('Cancelling…');
    expect((q('job-cancel') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows a reconnecting chip while live updates are down', async () => {
    const { fixture, q } = await render(status());
    expect(q('reconnecting')).toBeNull();
    jobs.connectionState.set('reconnecting');
    await fixture.whenStable();
    expect(q('reconnecting')!.textContent).toContain('Live updates reconnecting');
  });
});
