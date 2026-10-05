import { DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { PagedDto } from '../core/paging.models';
import { SenderKindChip } from '../senders/sender-kind-chip.component';
import { SenderKind } from '../senders/senders.models';
import { label, PAGE_SIZES, SenderPolicyDto } from './policies.models';

/** One page of policies; the name links to the detail. Paging is the page's. */
@Component({
  selector: 'app-policy-list',
  imports: [
    DecimalPipe,
    MatChipsModule,
    MatPaginatorModule,
    MatTableModule,
    PercentPipe,
    RouterLink,
    SenderKindChip,
  ],
  template: `
    <div class="overflow-x-auto">
      <table
        mat-table
        [dataSource]="page().items"
        [trackBy]="trackRow"
        class="w-full"
        aria-label="Policies, most mail first"
      >
        <ng-container matColumnDef="sender">
          <th mat-header-cell *matHeaderCellDef>Sender</th>
          <td mat-cell *matCellDef="let row" data-testid="cell-sender">
            <div class="flex min-w-0 items-center gap-2 py-1">
              <span class="scope" data-testid="scope-chip">{{ row.scope }}</span>
              <a
                class="flex min-w-0 flex-col"
                [routerLink]="['/policies', row.id]"
                data-testid="policy-link"
              >
                <span>{{ row.displayName || row.scopeKey }}</span>
                @if (row.displayName) {
                  <span class="muted text-sm">{{ row.scopeKey }}</span>
                }
              </a>
            </div>
          </td>
        </ng-container>
        <ng-container matColumnDef="messages">
          <th mat-header-cell *matHeaderCellDef>Messages</th>
          <td mat-cell *matCellDef="let row">{{ row.messageCount | number }}</td>
        </ng-container>
        <ng-container matColumnDef="kind">
          <th mat-header-cell *matHeaderCellDef>Kind</th>
          <td mat-cell *matCellDef="let row"><app-sender-kind-chip [kind]="kindOf(row)" /></td>
        </ng-container>
        <ng-container matColumnDef="unread">
          <th mat-header-cell *matHeaderCellDef>Unread</th>
          <td mat-cell *matCellDef="let row">{{ row.unreadRatio | percent }}</td>
        </ng-container>
        <ng-container matColumnDef="label">
          <th mat-header-cell *matHeaderCellDef>Label</th>
          <td mat-cell *matCellDef="let row" data-testid="cell-label">
            @if (row.isMixed) {
              <span data-testid="mixed"
                >mixed · {{ row.ruleCount }} {{ row.ruleCount === 1 ? 'rule' : 'rules' }}</span
              >
            } @else {
              {{ row.topicLabel }}
            }
            @if (row.documentTypeLabel) {
              <div class="muted text-sm">{{ row.documentTypeLabel }}</div>
            }
          </td>
        </ng-container>
        <ng-container matColumnDef="mailType">
          <th mat-header-cell *matHeaderCellDef>Mail type</th>
          <td mat-cell *matCellDef="let row">{{ label(row.mailType) }}</td>
        </ng-container>
        <ng-container matColumnDef="action">
          <th mat-header-cell *matHeaderCellDef>Action</th>
          <td mat-cell *matCellDef="let row">{{ row.action }}</td>
        </ng-container>
        <ng-container matColumnDef="confidence">
          <th mat-header-cell *matHeaderCellDef>Confidence</th>
          <td mat-cell *matCellDef="let row">{{ row.confidence | percent }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" data-testid="policy-row"></tr>
      </table>
    </div>
    <mat-paginator
      [length]="page().total"
      [pageIndex]="page().page - 1"
      [pageSize]="page().pageSize"
      [pageSizeOptions]="pageSizes"
      (page)="paged.emit($event)"
      aria-label="Policies pages"
    />
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .scope {
      padding: 0 0.5rem;
      border-radius: 9999px;
      font: var(--mat-sys-label-medium);
      line-height: 1.5rem;
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PolicyList {
  readonly page = input.required<PagedDto<SenderPolicyDto>>();
  readonly paged = output<PageEvent>();

  readonly columns = [
    'sender',
    'messages',
    'kind',
    'unread',
    'label',
    'mailType',
    'action',
    'confidence',
  ];
  readonly pageSizes = PAGE_SIZES;
  readonly label = label;
  readonly trackRow = (_: number, row: SenderPolicyDto) => row.id;

  kindOf(row: SenderPolicyDto): SenderKind {
    return row.kind as SenderKind;
  }
}
