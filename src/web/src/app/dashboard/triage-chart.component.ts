import { formatDate, formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import type { ChartData, ChartOptions } from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';
import { ChartPanel } from '../core/charts/chart-panel.component';
import { ChartPalette, ChartTheme, cartesianOptions, chartAxis } from '../core/charts/chart-theme';
import { TriagePoint, triageChartSummary, triageChartTable } from './triage.models';

/** Chart height in pixels; the width follows the container. */
export const CHART_HEIGHT = 220;

/** Unread on the left count axis, both coverages on the right 0–100 axis; one point per day. */
export function triageChartData(
  points: readonly TriagePoint[],
  p: ChartPalette,
  locale: string,
): ChartData<'line'> {
  // Lines need at least two points; a lone day is drawn as dots.
  const pointRadius = points.length === 1 ? 3 : 0;
  const series = (
    label: string,
    colour: string,
    yAxisID: string,
    data: number[],
    borderDash: number[] = [],
  ) => ({
    label,
    data,
    yAxisID,
    borderColor: colour,
    backgroundColor: colour,
    fill: false,
    tension: 0,
    borderWidth: 2,
    borderDash,
    pointRadius,
    pointHoverRadius: 4,
  });
  return {
    labels: points.map((pt) => formatDate(pt.day, 'd MMM', locale)),
    datasets: [
      series(
        'Inbox unread',
        p.primary,
        'y',
        points.map((pt) => pt.unread),
      ),
      series(
        '% covered by policy',
        p.tertiary,
        'y1',
        points.map((pt) => pt.policyCoverage),
      ),
      series(
        '% covered by filter',
        p.secondary,
        'y1',
        points.map((pt) => pt.filterCoverage),
        // Dashed: M3's secondary shares primary's hue, so colour alone doesn't separate it from unread.
        [6, 4],
      ),
    ],
  };
}

/** Dashboard chart: inbox unread and % covered by policy and by filter over the snapshot history. */
@Component({
  selector: 'app-triage-chart',
  imports: [BaseChartDirective, ChartPanel],
  template: `
    <app-chart-panel
      heading="Daily history"
      [summary]="summary()"
      [table]="table()"
      emptyText="The chart fills in once snapshots exist; one is taken every hour and after each job."
    >
      <ng-template>
        <div class="relative" [style.height.px]="height">
          <canvas baseChart type="line" [data]="data()" [options]="options()"></canvas>
        </div>
      </ng-template>
    </app-chart-panel>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TriageChart {
  private readonly theme = inject(ChartTheme);
  private readonly locale = inject(LOCALE_ID);

  readonly points = input.required<readonly TriagePoint[]>();
  readonly height = CHART_HEIGHT;

  readonly summary = computed(() => triageChartSummary(this.points(), this.locale));
  readonly table = computed(() => triageChartTable(this.points(), this.locale));
  readonly data = computed(() => triageChartData(this.points(), this.theme.palette(), this.locale));
  readonly options = computed<ChartOptions<'line'>>(() => {
    const p = this.theme.palette();
    const base = cartesianOptions<'line'>(p, this.theme.reducedMotion());
    const axis = chartAxis(p);
    const percent = (n: number) => `${formatNumber(n, this.locale, '1.1-1')}%`;
    return {
      ...base,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        ...base.plugins,
        legend: {
          ...base.plugins?.legend,
          position: 'bottom',
          labels: { ...base.plugins?.legend?.labels, boxWidth: 12, boxHeight: 2, padding: 8 },
        },
        tooltip: {
          ...base.plugins?.tooltip,
          callbacks: {
            // Ticks drop the year; the title keeps it so days a year apart stay distinct.
            title: (items) =>
              items.length
                ? formatDate(this.points()[items[0].dataIndex].day, 'mediumDate', this.locale)
                : '',
            label: (item) =>
              item.dataset.yAxisID === 'y1'
                ? `${item.dataset.label}: ${percent(item.parsed.y ?? 0)}`
                : `${item.dataset.label}: ${formatNumber(item.parsed.y ?? 0, this.locale)}`,
          },
        },
      },
      scales: {
        x: {
          ...axis,
          type: 'category',
          ticks: { ...axis.ticks, autoSkip: true, maxTicksLimit: 6, maxRotation: 0 },
        },
        y: {
          type: 'linear',
          position: 'left',
          beginAtZero: true,
          ...axis,
          ticks: { ...axis.ticks, precision: 0 },
        },
        y1: {
          type: 'linear',
          position: 'right',
          min: 0,
          max: 100,
          ...axis,
          grid: { drawOnChartArea: false },
          ticks: { ...axis.ticks, stepSize: 50, callback: (v) => `${v}%` },
        },
      },
    };
  });
}
