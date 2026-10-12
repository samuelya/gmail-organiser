import { formatNumber } from '@angular/common';
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
import { ChartPanel, ChartTable } from '../core/charts/chart-panel.component';
import { ChartPalette, ChartTheme } from '../core/charts/chart-theme';
import { AnalysisSummaryDto } from './analysis.models';

type StatusKey = 'notAnalysed' | 'analysed' | 'approved' | 'rejected' | 'applied';
type PaletteColour = 'outline' | 'tertiary' | 'secondary' | 'error' | 'primary';

const SEGMENTS: readonly { key: StatusKey; label: string; colour: PaletteColour }[] = [
  { key: 'notAnalysed', label: 'Not analysed', colour: 'outline' },
  { key: 'analysed', label: 'Waiting for review', colour: 'tertiary' },
  { key: 'approved', label: 'Approved', colour: 'secondary' },
  { key: 'rejected', label: 'Rejected', colour: 'error' },
  { key: 'applied', label: 'Applied', colour: 'primary' },
];

function total(s: AnalysisSummaryDto): number {
  return SEGMENTS.reduce((sum, seg) => sum + s[seg.key], 0);
}

/** One horizontal bar, one stacked segment per analysis status. */
export function statusChartData(s: AnalysisSummaryDto, p: ChartPalette): ChartData<'bar'> {
  return {
    labels: ['Messages'],
    datasets: SEGMENTS.map((seg) => ({
      label: seg.label,
      data: [s[seg.key]],
      backgroundColor: p[seg.colour],
      stack: 'status',
    })),
  };
}

/** The same counts as a table; no rows when nothing has been fetched. */
export function statusChartTable(s: AnalysisSummaryDto): ChartTable {
  return {
    columns: ['Status', 'Messages'],
    rows: total(s) === 0 ? [] : SEGMENTS.map((seg) => [seg.label, s[seg.key]]),
  };
}

/** Numbers formatted for `locale`, as the table's `DecimalPipe` formats them. */
export function statusChartSummary(s: AnalysisSummaryDto, locale: string): string {
  const parts = SEGMENTS.map(
    (seg) => `${formatNumber(s[seg.key], locale)} ${seg.label.toLowerCase()}`,
  );
  return `Messages by analysis status: ${parts.join(', ')}.`;
}

/** Dashboard chart: messages by analysis status as one stacked horizontal bar. */
@Component({
  selector: 'app-analysis-status-chart',
  imports: [BaseChartDirective, ChartPanel],
  template: `
    <app-chart-panel
      heading="Messages by status"
      [summary]="chartSummary()"
      [table]="table()"
      emptyText="Nothing fetched yet."
    >
      <ng-template>
        <!-- Taller on phones, where the five-entry legend wraps to several rows. -->
        <div class="relative h-36 sm:h-24">
          <canvas baseChart type="bar" [data]="data()" [options]="options()"></canvas>
        </div>
      </ng-template>
    </app-chart-panel>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnalysisStatusChart {
  private readonly theme = inject(ChartTheme);
  private readonly locale = inject(LOCALE_ID);

  readonly summary = input.required<AnalysisSummaryDto>();

  readonly table = computed(() => statusChartTable(this.summary()));
  readonly chartSummary = computed(() => statusChartSummary(this.summary(), this.locale));
  readonly data = computed(() => statusChartData(this.summary(), this.theme.palette()));
  readonly options = computed<ChartOptions<'bar'>>(() => {
    const base = this.theme.cartesian();
    return {
      ...base,
      indexAxis: 'y',
      plugins: {
        ...base.plugins,
        legend: {
          ...base.plugins?.legend,
          position: 'bottom',
          labels: { ...base.plugins?.legend?.labels, boxWidth: 12, padding: 8 },
        },
      },
      scales: {
        x: { ...base.scales?.['x'], stacked: true, beginAtZero: true },
        y: {
          ...base.scales?.['y'],
          stacked: true,
          ticks: { display: false },
          grid: { display: false },
        },
      },
    };
  });
}
