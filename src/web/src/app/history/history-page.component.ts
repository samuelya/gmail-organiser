import { BreakpointObserver } from '@angular/cdk/layout';
import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { catchError, EMPTY, filter, map, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { openBatchDetail } from './batch-detail.component';
import {
  ActionBatchDto,
  DEFAULT_HISTORY_PAGE_SIZE,
  HISTORY_PAGE_SIZES,
  kindLabel,
  undoConfirmMessage,
} from './history.models';
import { HistoryService } from './history.service';

/** Below this width the table folds into two columns so Undo stays on screen. */
const NARROW_QUERY = '(max-width: 767.98px)';
const WIDE_COLUMNS = ['when', 'kind', 'description', 'messages', 'status'];
const NARROW_COLUMNS = ['description', 'status'];

/** A row's live job: the batch's own (an apply or undo still running) or the undo started from it. */
interface RowJob {
  job: JobDto | undefined;
  percent: number | null;
}

/** `/history`: paged action batches, a detail drawer per batch, and Undo with live progress. */
@Component({
  selector: 'app-history-page',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    PageHeader,
  ],
  templateUrl: './history-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    tr.batch {
      cursor: pointer;
    }
    tr.batch:hover {
      background: var(--mat-sys-surface-container-high);
    }
    .link {
      all: unset;
      cursor: pointer;
      color: var(--mat-sys-primary);
      overflow-wrap: anywhere;
    }
    .link:focus-visible {
      outline: 2px solid var(--mat-sys-primary);
      outline-offset: 2px;
    }
    .status {
      min-width: 7rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HistoryPage {
  private readonly history = inject(HistoryService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly loads = new Subject<{ page: number; pageSize: number }>();

  private readonly breakpoints = inject(BreakpointObserver);
  readonly narrow = toSignal(this.breakpoints.observe(NARROW_QUERY).pipe(map((s) => s.matches)), {
    initialValue: this.breakpoints.isMatched(NARROW_QUERY),
  });
  readonly columns = computed(() => (this.narrow() ? NARROW_COLUMNS : WIDE_COLUMNS));
  readonly pageSizes = HISTORY_PAGE_SIZES;
  readonly page = signal(1);
  readonly pageSize = signal(DEFAULT_HISTORY_PAGE_SIZE);
  readonly result = signal<PagedDto<ActionBatchDto> | null>(null);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  /** Batch ids whose undo request is in flight. */
  readonly requesting = signal<ReadonlySet<string>>(new Set());
  /** Batch id → job id of the undo started from this page, until that job finishes. */
  readonly undoJobs = signal<ReadonlyMap<string, string>>(new Map());

  readonly rows = computed(() => this.result()?.items ?? []);

  /** Live jobs per batch id; a row's undo job wins over its own. */
  readonly rowJobs = computed(() => {
    const map = new Map<string, RowJob>();
    const undo = this.undoJobs();
    for (const batch of this.rows()) {
      const undoJobId = undo.get(batch.id);
      if (undoJobId) {
        const job = this.jobs.job(undoJobId);
        // Until the hub reports it, the undo shows as starting.
        if (!job || isActiveJob(job))
          map.set(batch.id, { job, percent: progressPercent(job?.progress) });
        continue;
      }
      const job = batch.jobId ? this.jobs.job(batch.jobId) : undefined;
      if (job && isActiveJob(job))
        map.set(batch.id, { job, percent: progressPercent(job.progress) });
    }
    return map;
  });

  /** Job ids the page follows that are still active, or not reported by the hub yet. */
  private readonly followed = computed(() => {
    const ids = new Set<string>();
    for (const id of this.undoJobs().values()) ids.add(id);
    for (const batch of this.rows()) {
      const job = batch.jobId ? this.jobs.job(batch.jobId) : undefined;
      if (job && isActiveJob(job)) ids.add(job.id);
    }
    return ids;
  });

  constructor() {
    this.loads
      .pipe(
        switchMap(({ page, pageSize }) =>
          this.history.list(page, pageSize).pipe(
            catchError(() => {
              this.loading.set(false);
              this.loadFailed.set(true);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => this.onLoaded(page));

    // A followed job finishing changes the batches: reload, and stop following a finished undo.
    // A job the hub reported and then dropped (missing from a reconnect snapshot) also finished.
    let previous = new Set<string>();
    const reported = new Set<string>();
    effect(() => {
      const current = this.followed();
      const ended = [...current, ...previous].filter((id) => {
        const job = this.jobs.job(id);
        if (!job) return reported.has(id);
        reported.add(id);
        return !isActiveJob(job);
      });
      for (const id of ended) reported.delete(id);
      previous = new Set([...current].filter((id) => !ended.includes(id)));
      if (ended.length === 0) return;
      untracked(() => {
        const undo = this.undoJobs();
        if ([...undo.values()].some((id) => ended.includes(id))) {
          this.undoJobs.set(new Map([...undo].filter(([, id]) => !ended.includes(id))));
        }
        this.reload();
      });
    });

    // After a reconnect, job changes may have been missed.
    effect(() => {
      if (this.jobs.reconnects() > 0) untracked(() => this.reload());
    });

    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loads.next({ page: this.page(), pageSize: this.pageSize() });
  }

  onPage(event: PageEvent): void {
    const sizeChanged = event.pageSize !== this.pageSize();
    this.pageSize.set(event.pageSize);
    this.page.set(sizeChanged ? 1 : event.pageIndex + 1);
    this.reload();
  }

  openDetail(batch: ActionBatchDto): void {
    openBatchDetail(this.dialog, batch);
  }

  undo(batch: ActionBatchDto, event?: Event): void {
    event?.stopPropagation();
    openConfirm(this.dialog, {
      title: 'Undo this batch?',
      message: undoConfirmMessage(batch),
      confirm: 'Undo',
    })
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.requesting.update((s) => new Set(s).add(batch.id));
          return this.history.undo(batch.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (undoBatch) => {
          this.endRequest(batch.id);
          if (undoBatch.jobId) {
            this.undoJobs.update((m) => new Map(m).set(batch.id, undoBatch.jobId as string));
          }
          this.reload();
        },
        // The error interceptor shows the server's reason (409) in a snackbar.
        error: () => this.endRequest(batch.id),
      });
  }

  /** The Undo button is offered by the server's `canUndo` and paused while this batch has a live job. */
  undoDisabled(batch: ActionBatchDto): boolean {
    return this.requesting().has(batch.id) || this.rowJobs().has(batch.id);
  }

  kind(kind: string): string {
    return kindLabel(kind);
  }

  trackRow = (_: number, batch: ActionBatchDto) => batch.id;

  private onLoaded(page: PagedDto<ActionBatchDto>): void {
    this.loading.set(false);
    // A page past the end (the list shrank, or the size changed) falls back to the last page.
    const last = Math.max(1, Math.ceil(page.total / page.pageSize));
    if (page.items.length === 0 && page.page > last) {
      this.page.set(last);
      this.reload();
      return;
    }
    this.result.set(page);
    // An undo that finished unseen (e.g. while the hub was down) shows up here as undone.
    const undone = new Set(page.items.filter((b) => b.undoneAt).map((b) => b.id));
    const undo = this.undoJobs();
    if ([...undo.keys()].some((id) => undone.has(id))) {
      this.undoJobs.set(new Map([...undo].filter(([id]) => !undone.has(id))));
    }
  }

  private endRequest(id: string): void {
    this.requesting.update((s) => {
      const next = new Set(s);
      next.delete(id);
      return next;
    });
  }
}
