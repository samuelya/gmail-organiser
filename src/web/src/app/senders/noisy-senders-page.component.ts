import { DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
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
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, filter, finalize, map, Observable, of, Subject, switchMap } from 'rxjs';
import { plural } from '../clean-up/clean-up.models';
import { UnsubscribeButton } from '../clean-up/unsubscribe-button.component';
import { errorMessage } from '../core/error.interceptor';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { openArchiveConfirm } from './archive-confirm-dialog.component';
import { CategoryMixBar } from './category-mix-bar.component';
import { NoisyFilterBar, NoisyFilters } from './noisy-filter-bar.component';
import { SenderKindChip } from './sender-kind-chip.component';
import {
  DEFAULT_NOISY_QUERY,
  lastPage,
  NoisyQuery,
  noisyQueryParams,
  NoisySenderDto,
  PAGE_SIZES,
  parseNoisyQuery,
  SENDER_ARCHIVE_JOB,
} from './senders.models';
import { SendersService } from './senders.service';

/** A list or action error: the ProblemDetails title (and detail, e.g. the 422 refusal reason). */
function problemText(error: unknown): string {
  return error instanceof HttpErrorResponse ? errorMessage(error) : 'Something went wrong.';
}

/** `/senders/noisy`: high-volume, mostly unread senders, cleared in bulk without the LLM (Stage 0). */
@Component({
  selector: 'app-noisy-senders-page',
  imports: [
    CategoryMixBar,
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatChipsModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    NoisyFilterBar,
    PageHeader,
    PercentPipe,
    RouterLink,
    SenderKindChip,
    UnsubscribeButton,
  ],
  templateUrl: './noisy-senders-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .address {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NoisySendersPage {
  private readonly senders = inject(SendersService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly jobs = inject(JobsService);
  private readonly requests = new Subject<NoisyQuery>();

  readonly columns = [
    ...['select', 'sender', 'kind', 'total', 'unread', 'listUnsubscribe'],
    ...['categories', 'firstSeen', 'lastSeen', 'actions'],
  ];
  readonly pageSizes = PAGE_SIZES;
  /** The URL is the source of truth for the filters and the page. */
  readonly query = toSignal(this.route.queryParamMap.pipe(map(parseNoisyQuery)), {
    initialValue: DEFAULT_NOISY_QUERY,
  });
  readonly result = signal<PagedDto<NoisySenderDto> | null>(null);
  readonly loading = signal(false);
  /** Why the list could not be loaded; `null` when it was. */
  readonly loadError = signal<string | null>(null);
  /** Why the last Archive or Propose was refused (e.g. the 422 reason); `null` otherwise. */
  readonly actionError = signal<string | null>(null);
  /** An Archive or Propose request is in flight. */
  readonly busy = signal(false);
  readonly alsoUnsubscribe = signal(false);
  /** Canonical addresses ticked on this page. */
  readonly selection = signal<ReadonlySet<string>>(new Set());

  readonly rows = computed(() => this.result()?.items ?? []);
  readonly selectable = computed(() => this.rows().filter((r) => !r.hasApprovedPolicy));
  readonly selected = computed(() =>
    this.rows().filter((r) => this.selection().has(r.canonicalAddress)),
  );
  readonly allSelected = computed(
    () => this.selectable().length > 0 && this.selected().length === this.selectable().length,
  );
  /** The toolbar's actions need a selection, and wait for a running archive or request. */
  readonly actionsDisabled = computed(
    () => this.selected().length === 0 || this.busy() || !!this.trackedJobId(),
  );
  readonly trackRow = (_: number, row: NoisySenderDto) => row.canonicalAddress;

  /** The archive job being followed, from the hub or the API answer, whichever is newer. */
  readonly trackedJobId = signal<string | null>(null);
  private readonly startedJob = signal<JobDto | null>(null);
  private readonly finishedJobIds = new Set<string>();
  readonly job = computed(() => {
    const id = this.trackedJobId();
    if (!id) return null;
    const held = this.startedJob()?.id === id ? this.startedJob() : null;
    const live = this.jobs.job(id);
    return live ? newerJob(live, held) : held;
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));

  constructor() {
    this.requests
      .pipe(
        switchMap((query) =>
          this.senders.listNoisy(query).pipe(
            map((page): PagedDto<NoisySenderDto> | string => page),
            catchError((error: unknown) => of(problemText(error))),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.loading.set(false);
        if (typeof page === 'string') {
          this.loadError.set(page);
          return;
        }
        this.loadError.set(null);
        const query = this.query();
        // A bookmark past the end: go to the last page instead of an empty one.
        if (page.items.length === 0 && query.page > lastPage(page.total, query.pageSize)) {
          this.navigate({ page: lastPage(page.total, query.pageSize) }, true);
          return;
        }
        // Ticked rows that left the page are dropped.
        const ids = new Set(page.items.map((r) => r.canonicalAddress));
        this.selection.update((s) => new Set([...s].filter((id) => ids.has(id))));
        this.result.set(page);
      });
    effect(() => {
      const query = this.query();
      this.jobs.reconnects();
      untracked(() => this.load(query));
    });
    // An archive started elsewhere or before this page opened: follow it, so its progress shows.
    effect(() => {
      const active = this.jobs
        .activeJobs()
        .find((j) => j.type === SENDER_ARCHIVE_JOB && !this.finishedJobIds.has(j.id));
      if (active && !untracked(this.trackedJobId))
        untracked(() => this.trackedJobId.set(active.id));
    });
    effect(() => {
      const job = this.job();
      if (job && !isActiveJob(job)) untracked(() => this.finish(job));
    });
  }

  reload(): void {
    this.load(this.query());
  }

  onFilters(filters: NoisyFilters): void {
    this.navigate({ ...filters, page: 1 });
  }

  onPage(event: PageEvent): void {
    const pageSize = event.pageSize;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ page, pageSize });
  }

  isSelected(row: NoisySenderDto): boolean {
    return this.selection().has(row.canonicalAddress);
  }

  toggle(row: NoisySenderDto): void {
    if (row.hasApprovedPolicy) return;
    const next = new Set(this.selection());
    if (!next.delete(row.canonicalAddress)) next.add(row.canonicalAddress);
    this.selection.set(next);
  }

  toggleAll(): void {
    this.selection.set(
      this.allSelected() ? new Set() : new Set(this.selectable().map((r) => r.canonicalAddress)),
    );
  }

  archiveAll(): void {
    const selected = this.selected();
    if (this.actionsDisabled()) return;
    openArchiveConfirm(this.dialog, {
      senders: selected.length,
      messages: selected.reduce((sum, r) => sum + r.totalCount, 0),
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() =>
        this.run(
          this.senders.archiveSenders({
            canonicalAddresses: selected.map((r) => r.canonicalAddress),
          }),
          (job) => {
            this.selection.set(new Set());
            this.startedJob.set(job);
            this.trackedJobId.set(job.id);
            this.snackBar.open(
              `Archiving mail from ${plural(selected.length, 'sender')}…`,
              'Dismiss',
              {
                duration: 4000,
              },
            );
          },
        ),
      );
  }

  proposeToBeDeleted(): void {
    if (this.actionsDisabled()) return;
    const request = {
      canonicalAddresses: this.selected().map((r) => r.canonicalAddress),
      toBeDeleted: true as const,
      unsubscribe: this.alsoUnsubscribe(),
    };
    this.run(this.senders.proposeNoisy(request), (result) => {
      this.selection.set(new Set());
      const skipped = result.skippedProtected + result.skippedAlreadySuggested;
      this.snackBar
        .open(
          `${plural(result.created, 'suggestion')} created; ${skipped.toLocaleString()} skipped ` +
            `(${result.skippedProtected.toLocaleString()} protected, ` +
            `${result.skippedAlreadySuggested.toLocaleString()} already suggested).`,
          'Review',
          { duration: 10_000 },
        )
        .onAction()
        .subscribe(() => void this.router.navigate(['/review']));
    });
  }

  /** One action at a time; a refusal shows its ProblemDetails on the page. */
  private run<T>(request: Observable<T>, next: (value: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    request
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next,
        error: (error: unknown) => {
          if (error instanceof HttpErrorResponse && error.status === 422) {
            this.actionError.set(problemText(error));
          }
          // Other errors are shown by the error interceptor.
        },
      });
  }

  private finish(job: JobDto): void {
    this.finishedJobIds.add(job.id);
    this.trackedJobId.set(null);
    this.startedJob.set(null);
    this.reload();
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Archive finished.')
        : job.status === 'cancelled'
          ? `Archive cancelled. ${job.progress?.message ?? ''}`.trim()
          : `Archive failed${job.error ? `: ${job.error}` : '.'}`;
    this.snackBar
      .open(message, 'Undo', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(['/history']));
  }

  private load(query: NoisyQuery): void {
    this.loading.set(true);
    this.requests.next(query);
  }

  private navigate(patch: Partial<NoisyQuery>, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: noisyQueryParams({ ...this.query(), ...patch }),
      replaceUrl,
    });
  }
}
