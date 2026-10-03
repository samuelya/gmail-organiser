import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTooltipModule } from '@angular/material/tooltip';
import { PagedDto } from '../core/paging.models';
import { CleanupMessage, formatSize } from './clean-up.models';

/** One server-side page of the selected sender's delete-labelled messages, with row selection. */
@Component({
  selector: 'app-cleanup-message-table',
  imports: [DatePipe, MatCheckboxModule, MatIconModule, MatPaginatorModule, MatTooltipModule],
  template: `
    <div class="overflow-x-auto">
      <table class="w-full" data-testid="message-table">
        <thead>
          <tr>
            <th scope="col" class="w-10">
              <mat-checkbox
                [checked]="allChecked()"
                [indeterminate]="someChecked()"
                [disabled]="disabled() || ids().length === 0"
                (change)="toggleAll.emit($event.checked)"
                aria-label="Select all messages on this page"
                data-testid="select-page"
              />
            </th>
            <th scope="col">Date</th>
            <th scope="col">Message</th>
            <th scope="col" class="text-right">Size</th>
          </tr>
        </thead>
        <tbody>
          @for (m of result()?.items ?? []; track m.id) {
            <tr data-testid="message-row">
              <td>
                <mat-checkbox
                  [checked]="selected().has(m.id)"
                  [disabled]="disabled()"
                  (change)="toggleRow.emit(m.id)"
                  [aria-label]="'Select ' + (m.subject || 'message without subject')"
                  data-testid="message-check"
                />
              </td>
              <td class="whitespace-nowrap">{{ m.internalDate | date: 'mediumDate' }}</td>
              <td class="min-w-0">
                <div class="flex flex-wrap items-center gap-2">
                  <span class="subject" data-testid="message-subject">{{
                    m.subject || '(no subject)'
                  }}</span>
                  @if (m.inInbox) {
                    <span class="chip" data-testid="message-inbox">In inbox</span>
                  }
                  @if (m.protectedReason; as reason) {
                    <span
                      class="chip protected"
                      [matTooltip]="reason"
                      [attr.aria-label]="'Protected: ' + reason"
                      tabindex="0"
                      data-testid="message-protected"
                    >
                      <mat-icon aria-hidden="true" inline>shield</mat-icon> Protected
                    </span>
                  }
                </div>
                <div class="muted snippet" data-testid="message-snippet">{{ m.snippet }}</div>
              </td>
              <td class="whitespace-nowrap text-right">{{ size(m.sizeEstimate) }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
    @if ((result()?.total ?? 0) > (result()?.pageSize ?? 0)) {
      <mat-paginator
        [length]="result()?.total ?? 0"
        [pageIndex]="(result()?.page ?? 1) - 1"
        [pageSize]="result()?.pageSize ?? 0"
        hidePageSize
        (page)="onPage($event)"
        aria-label="Message pages"
      />
    }
  `,
  styles: `
    th {
      text-align: left;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-medium);
      padding: 0.5rem;
    }
    td {
      padding: 0.5rem;
      vertical-align: top;
      border-top: 1px solid var(--mat-sys-outline-variant);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .subject {
      font: var(--mat-sys-body-medium);
      overflow-wrap: anywhere;
    }
    .snippet {
      font: var(--mat-sys-body-small);
      overflow-wrap: anywhere;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 0.25rem;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      border-radius: 999px;
      padding: 0 0.5rem;
      font: var(--mat-sys-label-small);
    }
    .chip.protected {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CleanupMessageTable {
  readonly result = input<PagedDto<CleanupMessage> | null>(null);
  readonly selected = input<ReadonlySet<string>>(new Set());
  readonly disabled = input(false);
  readonly toggleRow = output<string>();
  /** `true` ticks every row on the page, `false` clears them. */
  readonly toggleAll = output<boolean>();
  readonly pageChange = output<number>();

  readonly ids = computed(() => (this.result()?.items ?? []).map((m) => m.id));
  private readonly checkedCount = computed(
    () => this.ids().filter((id) => this.selected().has(id)).length,
  );
  readonly allChecked = computed(
    () => this.ids().length > 0 && this.checkedCount() === this.ids().length,
  );
  readonly someChecked = computed(() => this.checkedCount() > 0 && !this.allChecked());
  readonly size = formatSize;

  onPage(event: PageEvent): void {
    this.pageChange.emit(event.pageIndex + 1);
  }
}
