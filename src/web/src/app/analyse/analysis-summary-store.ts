import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, Subject, switchMap } from 'rxjs';
import { JobsService } from '../core/jobs.service';
import { fetchJobStatusKey } from '../dashboard/fetch.models';
import { AnalysisSummaryDto, analysisJobsKey } from './analysis.models';
import { AnalysisService } from './analysis.service';

/**
 * The Dashboard's one `GET /api/analysis/summary`, shared by every card that shows it. Provided by the
 * page, so it lives (and refetches) only as long as the page.
 */
@Injectable()
export class AnalysisSummaryStore {
  private readonly api = inject(AnalysisService);
  private readonly jobs = inject(JobsService);
  private readonly refresh = new Subject<void>();

  /** The last summary that loaded; kept after a failed reload. */
  readonly summary = signal<AnalysisSummaryDto | null>(null);
  /** The latest request failed. */
  readonly loadFailed = signal(false);

  /** Counts change when an analysis or fetch job appears or changes status, or after a reconnect. */
  private readonly refreshKey = computed(
    () =>
      `${analysisJobsKey(this.jobs.jobs())}|${fetchJobStatusKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.refresh
      .pipe(
        switchMap(() => this.api.summary().pipe(catchError(() => of(null)))),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((summary) => {
        if (summary) this.summary.set(summary);
        this.loadFailed.set(!summary);
      });
    effect(() => {
      this.refreshKey();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.refresh.next();
  }
}
