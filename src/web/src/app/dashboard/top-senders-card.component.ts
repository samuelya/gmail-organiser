import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import type { ChartData, ChartOptions } from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';
import { map } from 'rxjs';
import { ChartPanel } from '../core/charts/chart-panel.component';
import { ChartPalette, ChartTheme, chartAxis } from '../core/charts/chart-theme';
import { SendersService } from '../senders/senders.service';
import { mailboxCounts } from './mailbox-counts';
import {
  TOP_SENDERS_QUERY,
  TopSender,
  senderLink,
  topSenders,
  topSendersSummary,
  topSendersTable,
  truncate,
} from './top-senders';

/** Pixels per bar plus room for the legend and axis; the width follows the container. */
const BAR_HEIGHT = 28;
const CHART_CHROME = 64;
/** Below this chart width the bar labels are cut to `NARROW_LABEL_MAX` characters. */
const NARROW_CHART = 480;
const NARROW_LABEL_MAX = 14;

/** One horizontal bar per sender, stacked unread then read. */
export function topSendersData(rows: readonly TopSender[], p: ChartPalette): ChartData<'bar'> {
  const series = (label: string, colour: string, data: number[]) => ({
    label,
    data,
    backgroundColor: colour,
    borderColor: colour,
    stack: 'mail',
    barThickness: 'flex' as const,
    maxBarThickness: 20,
  });
  return {
    labels: rows.map((r) => r.label),
    datasets: [
      series(
        'Unread',
        p.primary,
        rows.map((r) => r.unread),
      ),
      series(
        'Read',
        p.outline,
        rows.map((r) => r.read),
      ),
    ],
  };
}

/** Dashboard card: the senders with the most mail, each bar opening that sender on the Senders page. */
@Component({
  selector: 'app-top-senders-card',
  imports: [
    BaseChartDirective,
    ChartPanel,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  template: `
    <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-top-senders">
      <mat-card-content class="flex flex-col gap-4" data-testid="top-senders-card">
        <h2 id="dashboard-top-senders" class="card-title">Top senders</h2>

        @if (loadFailed()) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="top-senders-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            The top senders could not be loaded.
            <button mat-button type="button" (click)="load()">Retry</button>
          </p>
        } @else if (senders() === null) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading the top senders" />
        }

        @if (!loadFailed() && senders() !== null) {
          <app-chart-panel
            heading="Most mail"
            [summary]="summary()"
            [table]="table()"
            emptyText="No senders yet; fetch your mailbox first."
          >
            <ng-template>
              <div class="relative" [style.height.px]="height()">
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
export class TopSendersCard {
  private readonly sendersApi = inject(SendersService);
  private readonly theme = inject(ChartTheme);
  private readonly router = inject(Router);
  private readonly locale = inject(LOCALE_ID);
  /** Reloads when a fetch or analysis job changes status, not on every progress tick. */
  private readonly counts = mailboxCounts(() =>
    this.sendersApi.list(TOP_SENDERS_QUERY).pipe(map((page) => page.items)),
  );

  /** `FetchStatusDto.messagesStored`, for the summary's share; null until the page has the status. */
  readonly messagesStored = input<number | null>(null);

  /** Null until the first answer. */
  readonly senders = this.counts.value;
  readonly loadFailed = this.counts.loadFailed;

  readonly rows = computed(() => topSenders(this.senders() ?? []));
  readonly summary = computed(() =>
    topSendersSummary(this.rows(), this.messagesStored(), this.locale),
  );
  readonly table = computed(() => topSendersTable(this.rows()));
  readonly height = computed(() => this.rows().length * BAR_HEIGHT + CHART_CHROME);
  readonly data = computed(() => topSendersData(this.rows(), this.theme.palette()));
  readonly options = computed<ChartOptions<'bar'>>(() => {
    const p = this.theme.palette();
    const base = this.theme.cartesian() as ChartOptions<'bar'>;
    const axis = chartAxis(p);
    return {
      ...base,
      indexAxis: 'y',
      // A click anywhere on a sender's row counts, so short bars stay clickable.
      interaction: { mode: 'y', intersect: false },
      onClick: (_event, elements) => {
        const row = elements.length ? this.rows()[elements[0].index] : undefined;
        if (row) this.open(row.address);
      },
      onHover: (event, elements) => {
        const canvas = event.native?.target as HTMLElement | undefined;
        if (canvas) canvas.style.cursor = elements.length ? 'pointer' : 'default';
      },
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
            title: (items) => {
              const row = items.length ? this.rows()[items[0].dataIndex] : undefined;
              if (!row) return '';
              return row.name === row.address ? row.address : [row.name, row.address];
            },
            label: (item) =>
              `${item.dataset.label}: ${formatNumber(item.parsed.x ?? 0, this.locale)}`,
          },
        },
      },
      scales: {
        x: {
          ...axis,
          type: 'linear',
          stacked: true,
          beginAtZero: true,
          ticks: { ...axis.ticks, precision: 0, autoSkipPadding: 12 },
        },
        y: {
          ...axis,
          type: 'category',
          stacked: true,
          ticks: {
            ...axis.ticks,
            // On a phone-width chart the full label would crowd out the bars.
            callback(_value, index) {
              const label = String(this.getLabelForValue(index));
              return this.chart.width < NARROW_CHART ? truncate(label, NARROW_LABEL_MAX) : label;
            },
          },
        },
      },
    };
  });

  load(): void {
    this.counts.load();
  }

  open(address: string): void {
    const { link, queryParams } = senderLink(address);
    void this.router.navigate([link], { queryParams });
  }
}
