import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

/** A chart's data as a table: the text alternative a "Show table" toggle swaps in. */
export interface ChartTable {
  columns: string[];
  rows: (string | number)[][];
}

let nextId = 0;

/**
 * Generic chart frame: a heading, the projected `<canvas baseChart>` labelled by `summary`,
 * and a "Show table" toggle that swaps the chart for the same data as a table.
 * With no rows it shows `emptyText` instead; the caller renders no canvas then.
 */
@Component({
  selector: 'app-chart-panel',
  imports: [DecimalPipe, MatButtonModule],
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
                      {{ isNumber(cell) ? (cell | number) : cell }}
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
          <ng-content />
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

  readonly headingId = `chart-panel-${nextId++}`;
  readonly showTable = signal(false);
  readonly hasRows = computed(() => this.table().rows.length > 0);

  isNumber(cell: string | number): cell is number {
    return typeof cell === 'number';
  }
}
