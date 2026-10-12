import { formatNumber } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, LOCALE_ID } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import type { ActiveElement, ChartData, ChartOptions } from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';
import { ChartPanel } from '../core/charts/chart-panel.component';
import { ChartTheme, chartAxis } from '../core/charts/chart-theme';
import {
  categorySlices,
  categorySummary,
  categoryTable,
  MailboxBreakdownDto,
  mailTypeBars,
  mailTypeSummary,
  mailTypeTable,
} from './breakdown.models';
import { BreakdownService } from './breakdown.service';
import { mailboxCounts } from './mailbox-counts';

/** Doughnut height in pixels; the width follows the container. */
export const DOUGHNUT_HEIGHT = 220;
/** Pixels per mail-type bar, plus room for the axis. */
export const BAR_ROW_HEIGHT = 28;
const BAR_AXIS_HEIGHT = 40;

/** Dashboard card: messages per Gmail category tab and suggestions per mail type. */
@Component({
  selector: 'app-mailbox-mix-card',
  imports: [
    BaseChartDirective,
    ChartPanel,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  template: `
    <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-mailbox-mix">
      <mat-card-content class="flex flex-col gap-4" data-testid="mailbox-mix-card">
        <h2 id="dashboard-mailbox-mix" class="card-title">Mailbox mix</h2>

        @if (loadFailed()) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="mailbox-mix-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            The mailbox mix could not be {{ breakdown() ? 'refreshed' : 'loaded' }}.
            <button mat-button type="button" (click)="load()">Retry</button>
          </p>
        } @else if (!breakdown()) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading the mailbox mix" />
        }

        @if (breakdown()) {
          <div class="grid grid-cols-1 gap-6 lg:grid-cols-2">
            <app-chart-panel
              heading="Gmail categories"
              [summary]="categorySummary()"
              [table]="categoryTable()"
              emptyText="No mail fetched yet."
              data-testid="mailbox-mix-categories"
            >
              <ng-template>
                <div class="relative" [style.height.px]="doughnutHeight">
                  <canvas
                    baseChart
                    type="doughnut"
                    [data]="categoryData()"
                    [options]="categoryOptions()"
                  ></canvas>
                </div>
              </ng-template>
            </app-chart-panel>
            <app-chart-panel
              heading="Mail types"
              [summary]="mailTypeSummary()"
              [table]="mailTypeTable()"
              emptyText="No suggestions yet."
              data-testid="mailbox-mix-mail-types"
            >
              <ng-template>
                <div class="relative" [style.height.px]="barHeight()">
                  <canvas
                    baseChart
                    type="bar"
                    [data]="mailTypeData()"
                    [options]="mailTypeOptions()"
                  ></canvas>
                </div>
              </ng-template>
            </app-chart-panel>
          </div>
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
export class MailboxMixCard {
  private readonly api = inject(BreakdownService);
  private readonly router = inject(Router);
  private readonly theme = inject(ChartTheme);
  private readonly locale = inject(LOCALE_ID);
  private readonly counts = mailboxCounts<MailboxBreakdownDto>(() => this.api.get());

  readonly breakdown = this.counts.value;
  readonly loadFailed = this.counts.loadFailed;
  readonly doughnutHeight = DOUGHNUT_HEIGHT;

  readonly slices = computed(() => {
    const b = this.breakdown();
    return b ? categorySlices(b.categories) : [];
  });
  readonly bars = computed(() => {
    const b = this.breakdown();
    return b ? mailTypeBars(b) : [];
  });
  readonly categorySummary = computed(() => categorySummary(this.slices()));
  readonly categoryTable = computed(() => categoryTable(this.slices()));
  readonly mailTypeSummary = computed(() => mailTypeSummary(this.bars(), this.locale));
  readonly mailTypeTable = computed(() => mailTypeTable(this.bars()));
  readonly barHeight = computed(() => this.bars().length * BAR_ROW_HEIGHT + BAR_AXIS_HEIGHT);

  /** Tabs in the theme's palette order; slice borders in the surface colour keep neighbours apart. */
  readonly categoryData = computed<ChartData<'doughnut'>>(() => {
    const p = this.theme.palette();
    const slices = this.slices();
    return {
      labels: slices.map((s) => s.label),
      datasets: [
        {
          label: 'Messages',
          data: slices.map((s) => s.count),
          backgroundColor: [p.primary, p.secondary, p.tertiary, p.tertiaryContainer, p.outline],
          borderColor: p.surface,
          borderWidth: 2,
        },
      ],
    };
  });
  readonly categoryOptions = computed<ChartOptions<'doughnut'>>(() => {
    const base = this.theme.cartesian() as ChartOptions<'doughnut'>;
    return {
      ...base,
      scales: {},
      plugins: {
        ...base.plugins,
        legend: {
          ...base.plugins?.legend,
          position: 'bottom',
          labels: { ...base.plugins?.legend?.labels, boxWidth: 12, padding: 8 },
        },
        tooltip: {
          ...base.plugins?.tooltip,
          callbacks: {
            label: (item) => {
              const slice = this.slices()[item.dataIndex];
              return slice
                ? `${slice.label}: ${formatNumber(slice.count, this.locale)} (${Math.round(slice.percent)}%)`
                : '';
            },
          },
        },
      },
    };
  });

  /** One series in `primary`: the label says what a bar is; "Unclassified" in `outline` as it isn't a link. */
  readonly mailTypeData = computed<ChartData<'bar'>>(() => {
    const p = this.theme.palette();
    const bars = this.bars();
    return {
      labels: bars.map((b) => b.label),
      datasets: [
        {
          label: 'Suggestions',
          data: bars.map((b) => b.count),
          backgroundColor: bars.map((b) => (b.classified ? p.primary : p.outline)),
          maxBarThickness: 20,
        },
      ],
    };
  });
  readonly mailTypeOptions = computed<ChartOptions<'bar'>>(() => {
    const p = this.theme.palette();
    const base = this.theme.cartesian();
    const axis = chartAxis(p);
    return {
      ...base,
      indexAxis: 'y',
      plugins: { ...base.plugins, legend: { display: false } },
      scales: {
        x: { ...axis, beginAtZero: true, ticks: { ...axis.ticks, precision: 0 } },
        y: { ...axis, grid: { display: false } },
      },
      onClick: (_event, elements) => this.openBar(elements),
      onHover: (event, elements) => {
        const canvas = event.native?.target as HTMLElement | undefined;
        if (canvas) canvas.style.cursor = this.barAt(elements)?.link ? 'pointer' : 'default';
      },
    };
  });

  load(): void {
    this.counts.load();
  }

  /** Opens the review page filtered to the clicked bar's type; "Unclassified" goes nowhere. */
  openBar(elements: readonly ActiveElement[]): void {
    const link = this.barAt(elements)?.link;
    if (link) void this.router.navigate([link.link], { queryParams: link.queryParams });
  }

  private barAt(elements: readonly ActiveElement[]) {
    const index = elements[0]?.index;
    return index === undefined ? undefined : this.bars()[index];
  }
}
