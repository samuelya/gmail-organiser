import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { catchError, of, Subject, switchMap } from 'rxjs';
import { AnalysisSummaryDto, analysisJobsKey } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { JobsService } from '../core/jobs.service';
import { FetchStatusDto, fetchJobsKey } from './fetch.models';
import { nextSteps } from './next-steps';

/** Dashboard card: up to three things to do next, each with a link to the page that does it. */
@Component({
  selector: 'app-next-steps-card',
  imports: [MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, RouterLink],
  template: `
    @if (loading() || steps().length > 0) {
      <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-next-steps">
        <mat-card-content class="flex flex-col gap-3" data-testid="next-steps-card">
          <h2 id="dashboard-next-steps" class="card-title">Next steps</h2>
          @if (loading()) {
            <mat-progress-bar mode="indeterminate" aria-label="Loading the next steps" />
          } @else {
            <ul class="m-0 flex list-none flex-col gap-2 p-0">
              @for (step of steps(); track step.id) {
                <li
                  class="flex flex-wrap items-center gap-2"
                  [attr.data-testid]="'step-' + step.id"
                >
                  <mat-icon aria-hidden="true">{{ step.icon }}</mat-icon>
                  <span class="min-w-0 flex-1">{{ step.text }}</span>
                  @if (step.link; as link) {
                    <a
                      mat-stroked-button
                      [routerLink]="link"
                      [queryParams]="step.queryParams"
                      [attr.data-testid]="'step-link-' + step.id"
                    >
                      {{ step.linkLabel }}
                    </a>
                  }
                </li>
              }
            </ul>
          }
        </mat-card-content>
      </mat-card>
    }
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextStepsCard {
  private readonly analysis = inject(AnalysisService);
  private readonly jobs = inject(JobsService);
  private readonly refresh = new Subject<void>();

  /** The page's fetch status; `null` until loaded. */
  readonly status = input<FetchStatusDto | null>(null);
  /** The page's last fetch status request failed. */
  readonly statusFailed = input(false);
  /** Gmail is connected; `null` while unknown. */
  readonly gmailConnected = input<boolean | null>(null);

  readonly summary = signal<AnalysisSummaryDto | null>(null);
  readonly summaryFailed = signal(false);

  /** Either input is still loading; a failed one counts as done and its steps are left out. */
  readonly loading = computed(
    () =>
      (this.status() === null && !this.statusFailed()) ||
      (this.summary() === null && !this.summaryFailed()),
  );
  readonly steps = computed(() =>
    nextSteps({
      gmailConnected: this.gmailConnected(),
      status: this.status(),
      summary: this.summary(),
    }),
  );

  /** The analysis summary card's trigger, plus a fetch job status or step change. */
  private readonly refreshKey = computed(
    () =>
      `${analysisJobsKey(this.jobs.jobs())}|${fetchJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.refresh
      .pipe(
        switchMap(() => this.analysis.summary().pipe(catchError(() => of(null)))),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((summary) => {
        if (summary) this.summary.set(summary);
        this.summaryFailed.set(!summary);
      });
    effect(() => {
      this.refreshKey();
      untracked(() => this.refresh.next());
    });
  }
}
