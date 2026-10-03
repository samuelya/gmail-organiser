import { DecimalPipe } from '@angular/common';
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
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { catchError, filter, finalize, map, Observable, of, switchMap, tap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { SendersService } from '../senders/senders.service';
import { SettingsService } from '../settings/settings.service';
import {
  CLEANUP_JOB,
  CLEANUP_MESSAGE_PAGE_SIZE,
  CLEANUP_SENDER_PAGE_SIZE,
  CleanupBatch,
  CleanupMessage,
  CleanupSelection,
  CleanupSender,
  CleanupSummary,
  DeleteConfirmData,
  plural,
  queuedMessage,
} from './clean-up.models';
import { CleanUpService } from './clean-up.service';
import { CleanupMessageTable } from './cleanup-message-table.component';
import { CleanupSenderList } from './cleanup-sender-list.component';
import { openDeleteConfirm } from './delete-confirm-dialog.component';

/** A queued clean-up batch being followed; `batchId` is null for a job started before this page opened. */
interface TrackedBatch {
  jobId: string;
  batchId: string | null;
  verb: string;
}

/**
 * `/clean-up`: the delete-labelled mail by sender on the left, the selected sender's messages on the
 * right. Nothing is updated optimistically: every action and finished job re-fetches what is shown.
 */
@Component({
  selector: 'app-clean-up-page',
  imports: [
    CleanupMessageTable,
    CleanupSenderList,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
    PageHeader,
    RouterLink,
  ],
  templateUrl: './clean-up-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .sender-title {
      font: var(--mat-sys-title-medium);
      overflow-wrap: anywhere;
    }
    dt {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    dd {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CleanUpPage {
  private readonly cleanUp = inject(CleanUpService);
  private readonly senderApi = inject(SendersService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly senderPageSize = CLEANUP_SENDER_PAGE_SIZE;

  private readonly search = signal('');
  private readonly senderPage = signal(1);
  readonly selected = signal<string | null>(null);
  private readonly messagePage = signal(1);
  /** Bumped after every action, finished job and reconnect: everything shown is re-fetched. */
  private readonly version = signal(0);

  readonly summary = signal<CleanupSummary | null>(null);
  readonly senders = signal<PagedDto<CleanupSender> | null>(null);
  readonly sendersLoading = signal(false);
  readonly sendersFailed = signal(false);
  readonly messages = signal<PagedDto<CleanupMessage> | null>(null);
  readonly messagesLoading = signal(false);
  /** Message ids ticked on the selected sender's current page. */
  readonly selection = signal<ReadonlySet<string>>(new Set());
  readonly busy = signal(false);

  private readonly settings = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(catchError(() => of(null))),
    { initialValue: null },
  );
  /** The delete label's name from settings; blank until they load. */
  readonly labelName = computed(() => this.settings()?.deleteLabelName ?? '');

  readonly sender = computed(() => {
    const address = this.selected();
    return this.senders()?.items.find((s) => s.address === address) ?? null;
  });
  readonly selectedProtected = computed(
    () =>
      (this.messages()?.items ?? []).filter((m) => m.protectedReason && this.selection().has(m.id))
        .length,
  );
  readonly empty = computed(() => this.summary()?.messages === 0);

  /** The batch's job, from the hub or (after a reconnect) from the API, whichever is newer. */
  readonly tracked = signal<TrackedBatch | null>(null);
  private readonly fetchedJob = signal<JobDto | null>(null);
  /** Jobs already reported; the hub may still hold one as active until its next snapshot. */
  private readonly finishedJobIds = new Set<string>();
  readonly job = computed(() => {
    const id = this.tracked()?.jobId;
    if (!id) return null;
    const fetched = this.fetchedJob();
    const held = fetched?.id === id ? fetched : null;
    const live = this.jobs.job(id);
    return live ? newerJob(live, held) : held;
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));

  constructor() {
    toObservable(this.version)
      .pipe(
        switchMap(() => this.cleanUp.summary().pipe(orNull())),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((summary) => this.summary.set(summary));

    const sendersKey = computed(() => ({
      search: this.search(),
      page: this.senderPage(),
      version: this.version(),
    }));
    toObservable(sendersKey)
      .pipe(
        tap(() => this.sendersLoading.set(true)),
        switchMap((k) =>
          this.cleanUp.senders(k.page, CLEANUP_SENDER_PAGE_SIZE, k.search).pipe(orNull()),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.sendersLoading.set(false);
        this.sendersFailed.set(!page);
        this.senders.set(page);
        if (page && !page.items.some((s) => s.address === this.selected())) {
          this.selected.set(page.items[0]?.address ?? null);
          this.resetMessages();
        }
      });

    const messagesKey = computed(() => ({
      address: this.selected(),
      page: this.messagePage(),
      version: this.version(),
    }));
    toObservable(messagesKey)
      .pipe(
        tap((k) => this.messagesLoading.set(!!k.address)),
        switchMap((k) =>
          k.address
            ? this.cleanUp.messages(k.address, k.page, CLEANUP_MESSAGE_PAGE_SIZE).pipe(orNull())
            : of(null),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((messages) => {
        this.messagesLoading.set(false);
        this.messages.set(messages);
        // Ticked rows that left the list (removed or trashed meanwhile) are dropped.
        const ids = new Set(messages?.items.map((m) => m.id) ?? []);
        this.selection.update((s) => new Set([...s].filter((id) => ids.has(id))));
      });

    // A clean-up job started elsewhere or before this page opened: follow it, so its progress shows
    // and no second batch is queued meanwhile.
    effect(() => {
      const active = this.jobs
        .activeJobs()
        .find((j) => j.type === CLEANUP_JOB && !this.finishedJobIds.has(j.id));
      if (active && !this.tracked())
        untracked(() => this.track({ jobId: active.id, batchId: null, verb: 'Clean-up' }));
    });
    effect(() => {
      const job = this.job();
      if (job && !isActiveJob(job)) untracked(() => this.finish(job));
    });
    // Updates may have been missed while disconnected; the hub's snapshot holds active jobs only.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      untracked(() => {
        this.refresh();
        const id = this.tracked()?.jobId;
        if (id) {
          this.cleanUp
            .job(id)
            .pipe(orNull(), takeUntilDestroyed(this.destroyRef))
            .subscribe((job) => job && this.fetchedJob.set(job));
        }
      });
    });
  }

  onSearch(search: string): void {
    this.search.set(search);
    this.senderPage.set(1);
  }

  onSenderPage(page: number): void {
    this.senderPage.set(page);
  }

  selectSender(address: string): void {
    if (address === this.selected()) return;
    this.selected.set(address);
    this.resetMessages();
  }

  onMessagePage(page: number): void {
    this.messagePage.set(page);
    this.selection.set(new Set());
  }

  toggle(id: string): void {
    this.selection.update((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  toggleAll(checked: boolean): void {
    this.selection.set(new Set(checked ? (this.messages()?.items.map((m) => m.id) ?? []) : []));
  }

  removeSelected(): void {
    const ids = [...this.selection()];
    if (ids.length > 0) this.unmark({ kind: 'ids', ids });
  }

  removeSender(): void {
    const s = this.sender();
    if (!s) return;
    openConfirm(this.dialog, {
      title: 'Remove sender from the list?',
      message: `${plural(s.count, 'message')} from ${s.displayName || s.address} lose the “${this.labelName()}” label. Undo from History.`,
      confirm: 'Remove from list',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.unmark({ kind: 'sender', address: s.address }));
  }

  deleteSelected(): void {
    const ids = [...this.selection()];
    if (ids.length === 0) return;
    this.confirmDelete(
      { kind: 'ids', ids },
      { scope: 'the selection', count: ids.length, protectedCount: this.selectedProtected() },
    );
  }

  deleteSender(): void {
    const s = this.sender();
    if (!s) return;
    this.confirmDelete(
      { kind: 'sender', address: s.address },
      { scope: s.displayName || s.address, count: s.count, protectedCount: s.protectedCount },
    );
  }

  deleteAll(): void {
    const s = this.summary();
    if (!s || s.messages === 0) return;
    this.confirmDelete(
      { kind: 'all' },
      { scope: 'every sender', count: s.messages, protectedCount: s.protected },
    );
  }

  allowlist(): void {
    const s = this.sender();
    if (!s || s.allowlisted) return;
    this.run(this.senderApi.setAllowlisted(s.address, true), () =>
      this.snackBar.open(
        `${s.address} is allowlisted; its messages are protected from Delete.`,
        'Dismiss',
        { duration: 6000 },
      ),
    );
  }

  /** The API refuses (409, shown by the error interceptor) while a chunk is pending. */
  cancelJob(): void {
    const id = this.tracked()?.jobId;
    if (!id) return;
    openConfirm(this.dialog, {
      title: 'Cancel clean-up?',
      message:
        'It stops at its next checkpoint. Messages already changed stay changed; undo them in History.',
      confirm: 'Cancel clean-up',
    })
      .pipe(
        filter((confirmed) => confirmed),
        switchMap(() => this.jobs.cancel(id).pipe(catchError(() => of(undefined)))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  private confirmDelete(
    selection: CleanupSelection,
    counts: Omit<DeleteConfirmData, 'labelName'>,
  ): void {
    if (this.busy() || this.tracked()) return;
    openDeleteConfirm(this.dialog, { ...counts, labelName: this.labelName() })
      .pipe(
        filter((result) => !!result),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) =>
        this.start(this.cleanUp.delete(selection, result!.includeProtected), 'Moving to Trash'),
      );
  }

  private unmark(selection: CleanupSelection): void {
    if (this.tracked()) return;
    this.start(this.cleanUp.unmark(selection), 'Removing from the list');
  }

  /** Queues the batch and follows its job; 204 means no selected message qualified. */
  private start(request: Observable<CleanupBatch | null>, verb: string): void {
    this.run(request, (result) => {
      this.selection.set(new Set());
      if (!result) {
        this.snackBar.open('Nothing to do: no selected message qualifies.', 'Dismiss', {
          duration: 6000,
        });
        return;
      }
      this.snackBar.open(queuedMessage(verb, result), 'Dismiss', { duration: 6000 });
      this.track({ jobId: result.batch.jobId ?? '', batchId: result.batch.id, verb });
    });
  }

  private track(batch: TrackedBatch): void {
    if (!batch.jobId) return;
    this.fetchedJob.set(null);
    this.tracked.set(batch);
  }

  private finish(job: JobDto): void {
    const batchId = this.tracked()?.batchId ?? null;
    this.finishedJobIds.add(job.id);
    this.tracked.set(null);
    this.fetchedJob.set(null);
    this.refresh();
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Clean-up finished.')
        : job.status === 'cancelled'
          ? `Clean-up cancelled. ${job.progress?.message ?? ''}`.trim()
          : `Clean-up failed${job.error ? `: ${job.error}` : '.'}`;
    this.snackBar
      .open(message, 'View in History', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(batchId ? ['/history', batchId] : ['/history']));
  }

  /** Runs one action at a time, then re-fetches whatever happened (an error may mean the state moved). */
  private run<T>(request: Observable<T>, next?: (value: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    request
      .pipe(
        finalize(() => {
          this.busy.set(false);
          this.refresh();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      // The error interceptor shows the server's problem detail.
      .subscribe({ next: (value) => next?.(value), error: () => undefined });
  }

  private refresh(): void {
    this.version.update((v) => v + 1);
  }

  private resetMessages(): void {
    this.messagePage.set(1);
    this.selection.set(new Set());
  }
}

/** Errors become `null`; the error interceptor already told the user. */
function orNull<T>() {
  return (source: Observable<T>) =>
    source.pipe(
      map((value) => value as T | null),
      catchError(() => of(null)),
    );
}
