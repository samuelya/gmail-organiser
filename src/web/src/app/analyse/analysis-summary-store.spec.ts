import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { isActiveJob, JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { job } from '../dashboard/fetch.testing';
import { ANALYSIS_RUN_JOB, AnalysisSummaryDto } from './analysis.models';
import { AnalysisService } from './analysis.service';
import { AnalysisSummaryStore } from './analysis-summary-store';

const summary = { notAnalysed: 5, analysed: 2 } as AnalysisSummaryDto;

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
}

describe('AnalysisSummaryStore', () => {
  function setup(load: () => Observable<AnalysisSummaryDto>) {
    const jobs = new FakeJobs();
    const api = { summary: vi.fn(load) };
    TestBed.configureTestingModule({
      providers: [
        { provide: JobsService, useValue: jobs },
        { provide: AnalysisService, useValue: api },
        AnalysisSummaryStore,
      ],
    });
    const store = TestBed.inject(AnalysisSummaryStore);
    TestBed.tick();
    return { store, jobs, api };
  }

  it('loads once and reloads on an analysis or fetch job status change and a reconnect', () => {
    const { jobs, api, store } = setup(() => of(summary));
    expect(api.summary).toHaveBeenCalledTimes(1);
    expect(store.summary()).toEqual(summary);
    const run = job('running', { id: 'run-1', type: ANALYSIS_RUN_JOB, queue: 'analysis' });
    jobs.held.set([run]);
    TestBed.tick();
    expect(api.summary).toHaveBeenCalledTimes(2);
    jobs.held.set([run, job('running')]);
    TestBed.tick();
    expect(api.summary).toHaveBeenCalledTimes(3);
    jobs.held.set([
      run,
      job('running', { progress: { done: 50, total: 100, message: 'All mail' } }),
    ]);
    TestBed.tick();
    expect(api.summary).toHaveBeenCalledTimes(3);
    jobs.reconnects.set(1);
    TestBed.tick();
    expect(api.summary).toHaveBeenCalledTimes(4);
  });

  it('keeps the last summary and flags a failed reload', () => {
    let fail = false;
    const { store } = setup(() => (fail ? throwError(() => new Error('down')) : of(summary)));
    fail = true;
    store.load();
    expect(store.summary()).toEqual(summary);
    expect(store.loadFailed()).toBe(true);
  });
});
