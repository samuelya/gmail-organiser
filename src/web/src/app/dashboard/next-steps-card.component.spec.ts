import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, RouterLink } from '@angular/router';
import { By } from '@angular/platform-browser';
import { Observable, of, Subject, throwError } from 'rxjs';
import { ANALYSIS_RUN_JOB, AnalysisSummaryDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { isActiveJob, JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { FetchStatusDto } from './fetch.models';
import { job, status } from './fetch.testing';
import { NextStepsCard } from './next-steps-card.component';

const summary: AnalysisSummaryDto = {
  notAnalysed: 500,
  analysed: 12,
  approved: 3,
  rejected: 0,
  applied: 0,
  actionCount: 0,
  toBeDeletedCount: 0,
  totalLlmCalls: 0,
  totalMessagesCovered: 0,
  savedPercent: 0,
  labelledNotAnalysed: 0,
  alternatives: 0,
  policiesProposed: 0,
  promptTokens: 0,
  completionTokens: 0,
  llmSeconds: 0,
  triageCalls: 0,
  escalatedCalls: 0,
  packedMessages: 0,
  packRetries: 0,
};

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
}

describe('NextStepsCard', () => {
  async function render(
    load: () => Observable<AnalysisSummaryDto>,
    inputs: {
      status?: FetchStatusDto | null;
      statusFailed?: boolean;
      gmailConnected?: boolean | null;
    } = {
      status: status({ mailboxPhase: 'completed' }),
      gmailConnected: true,
    },
  ) {
    const jobs = new FakeJobs();
    const api = { summary: vi.fn(load) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: jobs },
        { provide: AnalysisService, useValue: api },
      ],
    });
    const fixture = TestBed.createComponent(NextStepsCard);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, jobs, api, el, q };
  }

  it('renders the steps with icons and links carrying the status filter', async () => {
    const { fixture, q } = await render(() => of(summary));
    expect(q('step-review')!.textContent).toContain('12 suggestions waiting for review.');
    expect(q('step-review')!.querySelector('mat-icon')!.textContent).toContain('fact_check');
    expect(q('step-apply')!.textContent).toContain('3 approved suggestions not applied');
    expect(q('step-analyse')).toBeNull();
    const router = TestBed.inject(Router);
    const links = fixture.debugElement
      .queryAll(By.directive(RouterLink))
      .map((d) => router.serializeUrl(d.injector.get(RouterLink).urlTree!));
    expect(links).toEqual(['/review?status=pending', '/review?status=approved']);
    expect(q('step-link-review')!.textContent).toContain('Review');
    expect(q('step-link-apply')!.textContent).toContain('Apply');
  });

  it('shows only a progress bar while the summary or status loads', async () => {
    const pending = new Subject<AnalysisSummaryDto>();
    const { fixture, el } = await render(() => pending);
    expect(el.querySelector('mat-progress-bar')).not.toBeNull();
    expect(el.querySelector('li')).toBeNull();
    pending.next(summary);
    fixture.componentRef.setInput('status', null);
    await fixture.whenStable();
    expect(el.querySelector('mat-progress-bar')).not.toBeNull();
    fixture.componentRef.setInput('status', status({ mailboxPhase: 'completed' }));
    await fixture.whenStable();
    expect(el.querySelector('mat-progress-bar')).toBeNull();
    expect(el.querySelectorAll('li')).toHaveLength(2);
  });

  it('shows the steps it can compute when the summary fails, without an error', async () => {
    const { q, el } = await render(() => throwError(() => new Error('down')), {
      status: status(),
      gmailConnected: true,
    });
    expect(el.querySelector('mat-progress-bar')).toBeNull();
    expect(q('step-fetch')!.textContent).toContain('Fetch your mailbox');
    expect(q('step-link-fetch')).toBeNull();
    expect(el.querySelector('[role="alert"]')).toBeNull();
  });

  it('shows the summary steps when the status fails', async () => {
    const { q } = await render(() => of(summary), { status: null, statusFailed: true });
    expect(q('step-review')).not.toBeNull();
  });

  it('renders nothing when no step applies yet', async () => {
    const { el } = await render(
      () => of({ ...summary, analysed: 0, approved: 0, notAnalysed: 0 }),
      {
        status: status({ accountMismatch: true }),
        gmailConnected: true,
      },
    );
    expect(el.querySelector('mat-card')).toBeNull();
  });

  it("says you're all caught up", async () => {
    const zero = { ...summary, notAnalysed: 0, analysed: 0, approved: 0 };
    const { q } = await render(() => of(zero));
    expect(q('step-caught-up')!.textContent).toContain("You're all caught up.");
  });

  it('reloads on an analysis or fetch job status change and a reconnect', async () => {
    const { fixture, jobs, api } = await render(() => of(summary));
    expect(api.summary).toHaveBeenCalledTimes(1);
    const run = job('running', { id: 'run-1', type: ANALYSIS_RUN_JOB, queue: 'analysis' });
    jobs.held.set([run]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(2);
    jobs.held.set([run, job('running')]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(3);
    jobs.held.set([run, job('running', { progress: { done: 50, total: 100, message: null } })]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(3);
    jobs.reconnects.set(1);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(4);
  });
});
