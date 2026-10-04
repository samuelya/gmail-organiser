import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { relativeTime } from '../senders/senders.models';
import { FilterProposals } from './filter-proposals.component';
import { actionChips, FilterDto, FilterListDto } from './rules.models';
import { RulesService } from './rules.service';

/** The Rules page's Filters tab: the synced Gmail filters with delete / restore, and the proposed filters. */
@Component({
  selector: 'app-filters-tab',
  imports: [
    DecimalPipe,
    FilterProposals,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    MatTableModule,
    MatTooltipModule,
  ],
  template: `
    <div class="flex flex-col gap-4 pt-4">
      <div class="flex flex-wrap items-center gap-3">
        <button
          mat-flat-button
          type="button"
          [disabled]="syncing()"
          (click)="sync()"
          data-testid="filters-sync"
        >
          <mat-icon aria-hidden="true">sync</mat-icon>Sync from Gmail
        </button>
        @if (list(); as l) {
          <span class="muted text-sm" data-testid="filters-status">
            {{ l.activeCount | number }} of {{ l.limit | number }} filters,
            {{ l.syncedAt ? 'synced ' + synced(l.syncedAt) : 'never synced' }}
          </span>
        }
        <mat-slide-toggle
          class="ml-auto"
          [checked]="showDeleted()"
          (change)="toggleDeleted($event.checked)"
          data-testid="filters-show-deleted"
          >Show deleted</mat-slide-toggle
        >
      </div>

      @if (loading() || syncing()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading filters" />
      }
      @if (loadFailed()) {
        <p class="m-0 flex items-center gap-2" role="alert" data-testid="filters-error">
          <mat-icon aria-hidden="true">error</mat-icon>
          The filters could not be loaded.
          <button mat-button type="button" (click)="reload()">Retry</button>
        </p>
      }

      @if (list(); as l) {
        @if (l.filters.length === 0) {
          <p class="muted m-0" role="status" data-testid="no-filters">
            {{
              l.syncedAt
                ? 'No filters.'
                : 'No filters yet. Sync from Gmail to list the existing ones.'
            }}
          </p>
        } @else {
          <div class="overflow-x-auto">
            <table mat-table [dataSource]="l.filters" [trackBy]="trackRow" class="w-full">
              <ng-container matColumnDef="criteria">
                <th mat-header-cell *matHeaderCellDef>Criteria</th>
                <td mat-cell *matCellDef="let f" class="min-w-48 py-2 break-all">
                  {{ f.criteriaSummary }}
                </td>
              </ng-container>
              <ng-container matColumnDef="actions">
                <th mat-header-cell *matHeaderCellDef>Actions</th>
                <td mat-cell *matCellDef="let f" class="py-2">
                  <mat-chip-set [attr.aria-label]="'Actions of ' + f.criteriaSummary">
                    @for (chip of chips(f); track chip) {
                      <mat-chip data-testid="filter-chip">{{ chip }}</mat-chip>
                    }
                  </mat-chip-set>
                </td>
              </ng-container>
              <ng-container matColumnDef="app">
                <th mat-header-cell *matHeaderCellDef><span class="sr-only">Created by the app</span></th>
                <td mat-cell *matCellDef="let f">
                  @if (f.createdByApp) {
                    <mat-icon
                      matTooltip="Created by this app"
                      aria-label="Created by this app"
                      data-testid="filter-by-app"
                      >auto_awesome</mat-icon
                    >
                  }
                </td>
              </ng-container>
              <ng-container matColumnDef="row">
                <th mat-header-cell *matHeaderCellDef><span class="sr-only">Row actions</span></th>
                <td mat-cell *matCellDef="let f" class="text-right whitespace-nowrap">
                  @if (!f.deletedAt) {
                    <button
                      mat-button
                      type="button"
                      [disabled]="busy().has(f.id)"
                      (click)="remove(f)"
                      [attr.aria-label]="'Delete filter ' + f.criteriaSummary"
                      data-testid="filter-delete"
                    >
                      Delete
                    </button>
                  } @else if (f.deletedByApp) {
                    <button
                      mat-button
                      type="button"
                      [disabled]="busy().has(f.id)"
                      (click)="restore(f)"
                      [attr.aria-label]="'Restore filter ' + f.criteriaSummary"
                      data-testid="filter-restore"
                    >
                      Restore
                    </button>
                  } @else {
                    <span class="muted text-sm">Deleted in Gmail</span>
                  }
                </td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr
                mat-row
                *matRowDef="let f; columns: columns"
                [class.deleted]="!!f.deletedAt"
                data-testid="filter-row"
              ></tr>
            </table>
          </div>
        }
      }

      <app-filter-proposals
        [propose]="propose()"
        (created)="reload()"
        (proposeHandled)="proposeHandled.emit()"
      />
    </div>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    tr.deleted {
      opacity: 0.6;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FiltersTab {
  private readonly rules = inject(RulesService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly propose = input<string>();
  readonly proposeHandled = output<void>();

  readonly columns = ['criteria', 'actions', 'app', 'row'];
  readonly list = signal<FilterListDto | null>(null);
  readonly showDeleted = signal(false);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly syncing = signal(false);
  /** Filter ids with a delete or restore in flight. */
  readonly busy = signal<ReadonlySet<string>>(new Set());
  private readonly now = signal(Date.now());
  readonly synced = (iso: string) => relativeTime(iso, this.now());
  readonly chips = actionChips;
  readonly trackRow = (_: number, f: FilterDto) => f.id;

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.rules
      .list(this.showDeleted())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.loading.set(false);
          this.now.set(Date.now());
          this.list.set(list);
        },
        error: () => {
          this.loading.set(false);
          this.loadFailed.set(true);
        },
      });
  }

  toggleDeleted(show: boolean): void {
    this.showDeleted.set(show);
    this.reload();
  }

  /** 503 (Gmail not connected or rate-limiting) shows in a snackbar via the error interceptor. */
  sync(): void {
    if (this.syncing()) return;
    this.syncing.set(true);
    this.rules
      .sync()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.syncing.set(false);
          this.snackBar.open(
            `Synced ${r.total} ${r.total === 1 ? 'filter' : 'filters'}: ${r.added} new, ${r.removed} removed.`,
            'Dismiss',
            { duration: 4000 },
          );
          this.reload();
        },
        error: () => this.syncing.set(false),
      });
  }

  remove(f: FilterDto): void {
    openConfirm(this.dialog, {
      title: 'Delete this filter?',
      message: `Deletes "${f.criteriaSummary}" in Gmail. Mail it already labelled keeps its labels.`,
      confirm: 'Delete',
    })
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.setBusy(f.id, true);
          return this.rules.delete(f.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.setBusy(f.id, false);
          this.reload();
        },
        // The error interceptor shows the 409 / 503 reason in a snackbar.
        error: () => this.setBusy(f.id, false),
      });
  }

  restore(f: FilterDto): void {
    this.setBusy(f.id, true);
    this.rules
      .restore(f.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.setBusy(f.id, false);
          this.reload();
        },
        error: () => this.setBusy(f.id, false),
      });
  }

  private setBusy(id: string, busy: boolean): void {
    this.busy.update((s) => {
      const next = new Set(s);
      if (busy) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}
