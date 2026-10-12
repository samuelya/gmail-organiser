import { formatDate, formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import type { ChartData, ChartOptions } from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';
import { catchError, of, Subject, switchMap } from 'rxjs';
import { AnalysisRunDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { ChartPalette, ChartTheme, cartesianOptions, chartAxis } from '../core/charts/chart-theme';
import { ChartPanel } from '../core/charts/chart-panel.component';
import { JobsService } from '../core/jobs.service';
import {
  RUN_COST_DATE,
  RUN_COST_FETCH,
  RunCost,
  endedAnalysisJobsKey,
  runCostSummary,
  runCostTable,
  runCosts,
  savedShare,
  savedText,
} from './run-cost';

/** Chart height in pixels; the width follows the container. */
export const RUN_COST_HEIGHT = 260;

/** Messages stacked by model, derived and from memory on the left axis; model seconds as a line on the right. */
export function runCostData(
  rows: readonly RunCost[],
  p: ChartPalette,
  locale: string,
): ChartData<'bar' | 'line'> {
  const bar = (label: string, colour: string, data: number[]) => ({
    type: 'bar' as const,
    label,
    data,
    yAxisID: 'y',
    backgroundColor: colour,
    borderColor: colour,
    stack: 'messages',
    maxBarThickness: 32,
    order: 1,
  });
  return {
    labels: rows.map((r) => formatDate(r.finishedAt, RUN_COST_DATE, locale)),
    datasets: [
      bar(
        'By model',
        p.primary,
        rows.map((r) => r.llm),
      ),
      bar(
        'Derived',
        p.tertiary,
        rows.map((r) => r.derived),
      ),
      bar(
        'From memory',
        p.secondary,
        rows.map((r) => r.memory),
      ),
      {
        type: 'line' as const,
        label: 'Model seconds',
        data: rows.map((r) => r.llmSeconds),
        yAxisID: 'y1',
        borderColor: p.error,
        backgroundColor: p.error,
        borderWidth: 2,
        borderDash: [6, 4],
        fill: false,
        tension: 0,
        pointRadius: 3,
        pointHoverRadius: 4,
        // Drawn over the bars.
        order: 0,
      },
    ],
  };
}

/** Dashboard card: per analyse run, where the messages went (model, derived, memory) and the model time. */
@Component({
  selector: 'app-run-cost-card',
  imports: [
    BaseChartDirective,
    ChartPanel,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  template: `
    <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-run-cost">
      <mat-card-content class="flex flex-col gap-4" data-testid="run-cost-card">
        <h2 id="dashboard-run-cost" class="card-title">Analysis runs</h2>

        @if (loadFailed()) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="run-cost-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            {{
              runs() === null
                ? 'The analysis runs could not be loaded.'
                : 'The analysis runs could not be refreshed; showing the last loaded runs.'
            }}
            <button mat-button type="button" (click)="load()">Retry</button>
          </p>
        } @else if (runs() === null) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading the analysis runs" />
        }

        @if (runs() !== null) {
          <app-chart-panel
            heading="Model vs derived vs memory"
            [summary]="summary()"
            [table]="table()"
            emptyText="No analysis runs yet."
          >
            <ng-template>
              <div class="relative" [style.height.px]="height">
                <canvas baseChart type="bar" [data]="data()" [options]="options()"></canvas>
              </div>
            </ng-template>
          </app-chart-panel>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunCostCard {
  private readonly analysis = inject(AnalysisService);
  private readonly theme = inject(ChartTheme);
  private readonly locale = inject(LOCALE_ID);
  private readonly jobs = inject(JobsService);
  private readonly refresh = new Subject<void>();

  /** The last list that loaded, kept after a failed reload; null until the first answer. */
  readonly runs = signal<AnalysisRunDto[] | null>(null);
  readonly loadFailed = signal(false);
  readonly height = RUN_COST_HEIGHT;

  readonly rows = computed(() => runCosts(this.runs() ?? []));
  readonly summary = computed(() => runCostSummary(this.rows(), this.locale));
  readonly table = computed(() => runCostTable(this.rows(), this.locale));
  // ng2-charts types `data` by the canvas type; the line dataset sets its own type.
  readonly data = computed(
    () => runCostData(this.rows(), this.theme.palette(), this.locale) as ChartData<'bar'>,
  );
  readonly options = computed<ChartOptions<'bar'>>(() => {
    const p = this.theme.palette();
    const base = cartesianOptions<'bar'>(p, this.theme.reducedMotion());
    const axis = chartAxis(p);
    const count = (n: number) => formatNumber(n, this.locale, '1.0-0');
    return {
      ...base,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        ...base.plugins,
        legend: {
          ...base.plugins?.legend,
          position: 'bottom',
          labels: { ...base.plugins?.legend?.labels, boxWidth: 12, padding: 8 },
        },
        tooltip: {
          ...base.plugins?.tooltip,
          // The line's `order` draws it on top and would list it first; keep the dataset order.
          itemSort: (a, b) => a.datasetIndex - b.datasetIndex,
          callbacks: {
            title: (items) => {
              const row = items.length ? this.rows()[items[0].dataIndex] : undefined;
              return row ? [formatDate(row.finishedAt, 'medium', this.locale), row.target] : '';
            },
            label: (item) =>
              item.dataset.yAxisID === 'y1'
                ? `${item.dataset.label}: ${formatNumber(item.parsed.y ?? 0, this.locale, '1.0-1')}`
                : `${item.dataset.label}: ${count(item.parsed.y ?? 0)}`,
            footer: (items) => {
              const row = items.length ? this.rows()[items[0].dataIndex] : undefined;
              const share = row ? savedShare([row]) : null;
              return share === null ? '' : `Needed no model call: ${savedText(share, this.locale)}`;
            },
          },
        },
      },
      scales: {
        x: {
          ...axis,
          type: 'category',
          stacked: true,
          ticks: { ...axis.ticks, autoSkip: true, maxTicksLimit: 6, maxRotation: 0 },
        },
        y: {
          ...axis,
          type: 'linear',
          position: 'left',
          stacked: true,
          beginAtZero: true,
          title: { display: true, text: 'Messages', color: p.text },
          ticks: { ...axis.ticks, precision: 0 },
        },
        y1: {
          ...axis,
          type: 'linear',
          position: 'right',
          beginAtZero: true,
          grid: { drawOnChartArea: false },
          title: { display: true, text: 'Seconds', color: p.text },
          ticks: { ...axis.ticks, precision: 0 },
        },
      },
    };
  });

  constructor() {
    this.refresh
      .pipe(
        switchMap(() =>
          this.analysis
            .listRuns(false, RUN_COST_FETCH, 'completed')
            .pipe(catchError(() => of(null))),
        ),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((result) => {
        if (result) this.runs.set(result);
        this.loadFailed.set(!result);
      });
    // Reloads when an analysis job ends, so a completed run appears, and after a reconnect.
    const key = computed(
      () => `${endedAnalysisJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
    );
    effect(() => {
      key();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loadFailed.set(false);
    this.refresh.next();
  }
}
