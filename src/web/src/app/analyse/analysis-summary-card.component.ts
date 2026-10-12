import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { modelCountersText, savingsText, usageText } from './analysis.models';
import { AnalysisSummaryStore } from './analysis-summary-store';
import { AnalysisStatusChart } from './analysis-status-chart.component';

/**
 * Dashboard card: messages by analysis status, Action and To-Be-Deleted counts, cumulative LLM savings,
 * usage and model counters. The summary comes from the page's `AnalysisSummaryStore`.
 */
@Component({
  selector: 'app-analysis-summary-card',
  imports: [
    AnalysisStatusChart,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    RouterLink,
  ],
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
          <app-analysis-status-chart [summary]="s" />
          <dl class="m-0 grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-6">
            <div>
              <dt>Not analysed</dt>
              <dd data-testid="not-analysed">{{ s.notAnalysed | number }}</dd>
            </div>
            <div>
              <dt>Waiting for review</dt>
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
          <p class="m-0" data-testid="labelled-not-analysed">
            Already labelled, not analysed: {{ s.labelledNotAnalysed | number }}
          </p>
          <p class="muted m-0" data-testid="savings">{{ savings() }}</p>
          @if (s.policiesProposed > 0) {
            <p class="muted m-0" data-testid="summary-policies">
              {{ s.policiesProposed | number }}
              {{ s.policiesProposed === 1 ? 'policy' : 'policies' }} proposed
            </p>
          }
          @if (usage(); as u) {
            <p class="muted m-0" data-testid="summary-usage">{{ u }}</p>
          }
          @if (counters(); as c) {
            <p class="muted m-0" data-testid="summary-counters">{{ c }}</p>
          }
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
  private readonly store = inject(AnalysisSummaryStore);

  readonly summary = this.store.summary.asReadonly();
  readonly loadFailed = this.store.loadFailed.asReadonly();
  readonly savings = computed(() => {
    const s = this.summary();
    return s
      ? `All runs: ${savingsText(s.totalLlmCalls, s.totalMessagesCovered, s.savedPercent)}`
      : '';
  });

  readonly usage = computed(() => {
    const s = this.summary();
    return s ? usageText(s) : null;
  });
  readonly counters = computed(() => {
    const s = this.summary();
    return s ? modelCountersText(s) : null;
  });

  load(): void {
    this.store.load();
  }
}
