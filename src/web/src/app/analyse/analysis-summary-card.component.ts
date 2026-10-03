import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { catchError, of, Subject, switchMap } from 'rxjs';
import { JobsService } from '../core/jobs.service';
import { AnalysisSummaryDto, analysisJobsKey, savingsText } from './analysis.models';
import { AnalysisService } from './analysis.service';

/** Dashboard card: messages by analysis status, Action and To-Be-Deleted counts, cumulative LLM savings. */
@Component({
  selector: 'app-analysis-summary-card',
  imports: [DecimalPipe, MatButtonModule, MatCardModule, MatIconModule, RouterLink],
  template: `
    <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-analysis">
      <mat-card-content class="flex flex-col gap-4" data-testid="analysis-card">
        <div class="flex flex-wrap items-center gap-2">
          <h2 id="dashboard-analysis" class="card-title flex-1">Analysis</h2>
          <a mat-button routerLink="/analyse" data-testid="link-analyse">Analyse</a>
          <a mat-button routerLink="/review" data-testid="link-review">Review</a>
        </div>
        @if (loadFailed()) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="analysis-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            The analysis summary could not be loaded.
            <button mat-button type="button" (click)="load()">Retry</button>
          </p>
        }
        @if (summary(); as s) {
          <dl class="m-0 grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
            <div>
              <dt>Not analysed</dt>
              <dd data-testid="not-analysed">{{ s.notAnalysed | number }}</dd>
            </div>
            <div>
              <dt>Analysed</dt>
              <dd data-testid="analysed">{{ s.analysed | number }}</dd>
            </div>
            <div>
              <dt>Approved</dt>
              <dd data-testid="approved">{{ s.approved | number }}</dd>
            </div>
            <div>
              <dt>Applied</dt>
              <dd data-testid="applied">{{ s.applied | number }}</dd>
            </div>
            <div>
              <dt>Action</dt>
              <dd data-testid="action-count">{{ s.actionCount | number }}</dd>
            </div>
            <div>
              <dt>To be deleted</dt>
              <dd data-testid="to-be-deleted">{{ s.toBeDeletedCount | number }}</dd>
              <dd class="dd-link">
                <a routerLink="/clean-up" data-testid="open-clean-up">Open Clean-up</a>
              </dd>
            </div>
          </dl>
          <p class="muted m-0" data-testid="savings">{{ savings() }}</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    dt {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    dd {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
    .dd-link {
      font: var(--mat-sys-body-small);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnalysisSummaryCard {
  private readonly analysis = inject(AnalysisService);
  private readonly jobs = inject(JobsService);
  private readonly refresh = new Subject<void>();

  readonly summary = signal<AnalysisSummaryDto | null>(null);
  readonly loadFailed = signal(false);
  readonly savings = computed(() => {
    const s = this.summary();
    return s
      ? `All runs: ${savingsText(s.totalLlmCalls, s.totalMessagesCovered, s.savedPercent)}`
      : '';
  });

  /** Counts change when an analysis job appears or changes status, or after a reconnect. */
  private readonly refreshKey = computed(
    () => `${analysisJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.refresh
      .pipe(
        switchMap(() => this.analysis.summary().pipe(catchError(() => of(null)))),
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
