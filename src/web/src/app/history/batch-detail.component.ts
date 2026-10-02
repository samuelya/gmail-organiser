import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActionBatchDetailDto, ActionBatchDto, kindLabel } from './history.models';
import { HistoryService } from './history.service';

export interface BatchDetailData {
  batch: ActionBatchDto;
}

/** Side drawer with a batch's log: subject and labels added / removed by name per message. */
@Component({
  selector: 'app-batch-detail',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatDialogModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  template: `
    <h2 mat-dialog-title>
      {{ kind(data.batch.kind) }} · {{ data.batch.createdAt | date: 'short' }}
    </h2>
    <mat-dialog-content class="flex flex-col gap-4">
      <p class="m-0 break-words" data-testid="detail-description">{{ data.batch.description }}</p>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading the batch" />
      }
      @if (failed()) {
        <p class="m-0 flex items-center gap-2" role="alert" data-testid="detail-error">
          <mat-icon aria-hidden="true">error</mat-icon>The batch could not be loaded.
          <button mat-button type="button" (click)="load()">Retry</button>
        </p>
      }
      @if (detail(); as d) {
        @if (d.createdLabels.length > 0) {
          <section class="note p-3" aria-labelledby="created-labels" data-testid="created-labels">
            <h3 id="created-labels" class="section-title">Labels this batch created</h3>
            <p class="m-0">{{ labelNames(d) }}</p>
            <p class="muted m-0 mt-1 text-sm">
              Undo keeps these labels. Remove them in Gmail if you no longer need them.
            </p>
          </section>
        }
        <ul class="m-0 flex list-none flex-col p-0" aria-label="Messages in this batch">
          @for (row of d.rows; track row.id) {
            <li class="row flex flex-col gap-1 py-2" data-testid="detail-row">
              <span class="break-words" [class.muted]="!row.subject">
                {{ row.subject ?? 'Message no longer stored' }}
              </span>
              @if (row.labelNamesAdded.length > 0) {
                <span class="text-sm" data-testid="row-added">
                  <span class="muted">Added:</span> {{ row.labelNamesAdded.join(', ') }}
                </span>
              }
              @if (row.labelNamesRemoved.length > 0) {
                <span class="text-sm" data-testid="row-removed">
                  <span class="muted">Removed:</span> {{ row.labelNamesRemoved.join(', ') }}
                </span>
              }
              @if (row.note) {
                <span class="muted text-sm" data-testid="row-note">{{ row.note }}</span>
              }
              @if (row.undoneByBatchId) {
                <span class="muted text-sm">Undone</span>
              }
            </li>
          } @empty {
            <li class="muted" data-testid="detail-empty">No messages were changed.</li>
          }
        </ul>
        @if (d.truncated) {
          <p class="muted m-0 text-sm" role="note" data-testid="detail-truncated">
            Showing the first {{ d.rows.length | number }} of
            {{ d.batch.messageCount | number }} messages.
          </p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .section-title {
      font: var(--mat-sys-title-small);
      margin: 0 0 4px;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .note {
      background: var(--mat-sys-surface-container-high);
      border-radius: var(--mat-sys-corner-medium);
    }
    .row + .row {
      border-top: 1px solid var(--mat-sys-outline-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BatchDetail {
  private readonly history = inject(HistoryService);
  private readonly destroyRef = inject(DestroyRef);
  readonly data = inject<BatchDetailData>(MAT_DIALOG_DATA);

  readonly detail = signal<ActionBatchDetailDto | null>(null);
  readonly loading = signal(false);
  readonly failed = signal(false);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.history
      .get(this.data.batch.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (d) => {
          this.detail.set(d);
          this.loading.set(false);
        },
        error: () => {
          this.failed.set(true);
          this.loading.set(false);
        },
      });
  }

  kind(kind: string): string {
    return kindLabel(kind);
  }

  labelNames(d: ActionBatchDetailDto): string {
    return d.createdLabels.map((l) => l.name).join(', ');
  }
}

/** Opens the detail as a full-height drawer on the right. */
export function openBatchDetail(
  dialog: MatDialog,
  batch: ActionBatchDto,
): MatDialogRef<BatchDetail> {
  return dialog.open<BatchDetail, BatchDetailData>(BatchDetail, {
    data: { batch },
    position: { right: '0', top: '0' },
    height: '100dvh',
    maxHeight: '100dvh',
    width: 'min(640px, 100vw)',
    maxWidth: '100vw',
    autoFocus: 'dialog',
  });
}
