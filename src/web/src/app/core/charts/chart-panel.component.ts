import { DecimalPipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  contentChild,
  input,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { ChartLinkCell, ChartTable } from './chart.models';

let nextId = 0;

/**
 * Generic chart frame: a heading, the chart labelled by `summary`, and a "Show table" toggle
 * that swaps the chart for the same data as a table. The caller passes the `<canvas baseChart>`
 * inside an `<ng-template>`, so the panel creates it only when there are rows; otherwise it
 * shows `emptyText`.
 */
@Component({
  selector: 'app-chart-panel',
  imports: [DecimalPipe, MatButtonModule, NgTemplateOutlet, RouterLink],
  template: `
    <section class="flex flex-col gap-2" [attr.aria-labelledby]="headingId">
      <div class="flex flex-wrap items-center gap-2">
        <h3 [id]="headingId" class="panel-title flex-1">{{ heading() }}</h3>
        @if (hasRows()) {
          <button
            mat-button
            type="button"
            [attr.aria-pressed]="showTable()"
            (click)="showTable.set(!showTable())"
            data-testid="chart-toggle"
          >
            {{ showTable() ? 'Show chart' : 'Show table' }}
          </button>
        }
      </div>
      @if (!hasRows()) {
        <p class="muted m-0" data-testid="chart-empty">{{ emptyText() }}</p>
      } @else {
        @if (showTable()) {
          <table class="chart-table" data-testid="chart-table">
            <caption class="sr-only">
              {{
                summary()
              }}
            </caption>
            <thead>
              <tr>
                @for (column of table().columns; track $index) {
                  <th scope="col">{{ column }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of table().rows; track $index) {
                <tr>
                  @for (cell of row; track $index) {
                    <td [class.num]="isNumber(cell)">
                      @if (isNumber(cell)) {
                        {{ cell | number }}
                      } @else if (isLink(cell)) {
                        <a [routerLink]="cell.link" [queryParams]="cell.queryParams">{{
                          cell.text
                        }}</a>
                      } @else {
                        {{ cell }}
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        }
        <div
          class="chart-canvas"
          role="img"
          [attr.aria-label]="summary()"
          [hidden]="showTable()"
          data-testid="chart-canvas"
        >
          <ng-container [ngTemplateOutlet]="chart() ?? null" />
        </div>
      }
    </section>
  `,
  styles: `
    .panel-title {
      font: var(--mat-sys-title-small);
      margin: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .chart-canvas {
      position: relative;
    }
    .chart-table {
      border-collapse: collapse;
      font: var(--mat-sys-body-medium);
    }
    .chart-table th,
    .chart-table td {
      padding: 0.25rem 1rem 0.25rem 0;
      text-align: start;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .chart-table th {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-medium);
    }
    .chart-table td.num {
      text-align: end;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChartPanel {
  readonly heading = input.required<string>();
  /** One sentence describing the chart: the canvas's text alternative. */
  readonly summary = input.required<string>();
  readonly table = input.required<ChartTable>();
  readonly emptyText = input.required<string>();

  /** The chart, created only while there are rows. */
  readonly chart = contentChild(TemplateRef);

  readonly headingId = `chart-panel-${nextId++}`;
  readonly showTable = signal(false);
  readonly hasRows = computed(() => this.table().rows.length > 0);

  isNumber(cell: string | number | ChartLinkCell): cell is number {
    return typeof cell === 'number';
  }

  isLink(cell: string | number | ChartLinkCell): cell is ChartLinkCell {
    return typeof cell === 'object';
  }
}
