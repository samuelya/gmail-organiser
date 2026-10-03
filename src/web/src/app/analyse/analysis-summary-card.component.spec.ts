import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { isActiveJob, JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { AnalysisSummaryCard } from './analysis-summary-card.component';
import { ANALYSIS_RUN_JOB, AnalysisSummaryDto } from './analysis.models';
import { AnalysisService } from './analysis.service';

const summary: AnalysisSummaryDto = {
  notAnalysed: 1200,
  analysed: 300,
  approved: 40,
  rejected: 2,
  applied: 25,
  actionCount: 7,
  toBeDeletedCount: 90,
  totalLlmCalls: 60,
  totalMessagesCovered: 300,
  savedPercent: 0.8,
};

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
}

describe('AnalysisSummaryCard', () => {
  async function render(load: () => ReturnType<AnalysisService['summary']>) {
    const jobs = new FakeJobs();
    const api = { summary: vi.fn(load) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: jobs },
        { provide: AnalysisService, useValue: api },
      ],
    });
    const fixture = TestBed.createComponent(AnalysisSummaryCard);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, jobs, api, q };
  }

  it('renders the counts, the cumulative savings and links to Analyse and Review', async () => {
    const { q } = await render(() => of(summary));
    expect(q('not-analysed')!.textContent).toContain('1,200');
    expect(q('analysed')!.textContent).toContain('300');
    expect(q('approved')!.textContent).toContain('40');
    expect(q('applied')!.textContent).toContain('25');
    expect(q('action-count')!.textContent).toContain('7');
    expect(q('to-be-deleted')!.textContent).toContain('90');
    expect(q('savings')!.textContent).toContain('60 LLM calls for 300 emails (80 % saved)');
    expect(q('link-analyse')!.getAttribute('href')).toBe('/analyse');
    expect(q('link-review')!.getAttribute('href')).toBe('/review');
    expect(q('open-clean-up')!.getAttribute('href')).toBe('/clean-up');
    expect(q('open-clean-up')!.textContent).not.toMatch(/\d/);
  });

  it('reloads when an analysis job changes status, not for other jobs', async () => {
    const { fixture, jobs, api } = await render(() => of(summary));
    expect(api.summary).toHaveBeenCalledTimes(1);
    const base: JobDto = {
      id: 'job-1',
      type: ANALYSIS_RUN_JOB,
      queue: 'analysis',
      status: 'running',
      progress: null,
      error: null,
      createdAt: '2026-01-01T00:00:00Z',
      startedAt: null,
      updatedAt: '2026-01-01T00:00:00Z',
      finishedAt: null,
      version: 1,
    };
    jobs.held.set([base, { ...base, id: 'job-2', type: 'mailbox_fetch' }]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(2);
    jobs.held.set([base, { ...base, id: 'job-2', type: 'mailbox_fetch', status: 'completed' }]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(2);
    jobs.held.set([{ ...base, status: 'completed', version: 2 }]);
    await fixture.whenStable();
    expect(api.summary).toHaveBeenCalledTimes(3);
  });

  it('shows an error with Retry when the summary fails', async () => {
    const { q } = await render(() => throwError(() => new Error('down')));
    expect(q('analysis-error')).not.toBeNull();
  });
});
