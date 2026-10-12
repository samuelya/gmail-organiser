import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, RouterLink } from '@angular/router';
import { By } from '@angular/platform-browser';
import { Observable, of, Subject, throwError } from 'rxjs';
import { AnalysisSummaryStore } from '../analyse/analysis-summary-store';
import { AnalysisSummaryDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { isActiveJob, JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { FetchStatusDto } from './fetch.models';
import { status } from './fetch.testing';
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
      gmailFailed?: boolean;
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
        AnalysisSummaryStore,
      ],
    });
    const fixture = TestBed.createComponent(NextStepsCard);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, jobs, api, el, q, store: TestBed.inject(AnalysisSummaryStore) };
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
    const { q } = await render(() => of(summary), {
      status: null,
      statusFailed: true,
      gmailConnected: true,
    });
    expect(q('step-review')).not.toBeNull();
  });

  it('leaves out the stale summary after a failed reload, never saying caught up', async () => {
    const zero = { ...summary, notAnalysed: 0, analysed: 0, approved: 0 };
    let fail = false;
    const { fixture, q, store } = await render(() =>
      fail ? throwError(() => new Error('down')) : of(zero),
    );
    expect(q('step-caught-up')).not.toBeNull();
    fail = true;
    store.load();
    await fixture.whenStable();
    expect(q('step-caught-up')).toBeNull();
  });

  it('leaves out the stale status after a failed reload', async () => {
    const zero = { ...summary, notAnalysed: 0, analysed: 0, approved: 0 };
    const { fixture, q } = await render(() => of(zero), { status: status(), gmailConnected: true });
    expect(q('step-fetch')).not.toBeNull();
    fixture.componentRef.setInput('statusFailed', true);
    await fixture.whenStable();
    expect(q('step-fetch')).toBeNull();
  });

  it('waits for the Gmail connection, and shows no fetch step when its check fails', async () => {
    const { fixture, q, el } = await render(() => of(summary), {
      status: status(),
      gmailConnected: null,
    });
    expect(el.querySelector('mat-progress-bar')).not.toBeNull();
    fixture.componentRef.setInput('gmailFailed', true);
    await fixture.whenStable();
    expect(el.querySelector('mat-progress-bar')).toBeNull();
    expect(q('step-fetch')).toBeNull();
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
});
