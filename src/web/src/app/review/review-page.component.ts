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
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { catchError, filter, finalize, map, Observable, of, switchMap, tap } from 'rxjs';
import { ExternalReviewDto } from '../core/claude.models';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { ANALYSIS_LIMITS } from '../settings/settings.models';
import { SettingsService } from '../settings/settings.service';
import { openBulkApprove } from './bulk-approve-dialog.component';
import { ClaudeSenderActions } from './claude-verdict.component';
import { GroupCard } from './group-card.component';
import {
  APPLY_JOB,
  canApplyRest,
  DEFAULT_FLAG_LABELS,
  GROUP_PAGE_SIZE,
  GroupDecisionResponse,
  MAX_ANALYSE_INDIVIDUALLY,
  outcomeOf,
  patchClaudeReview,
  patternSummary,
  REVIEW_EDIT_DIALOG,
  ReviewGroupDto,
  ReviewSenderDetailDto,
  ReviewSenderDto,
  ReviewStatus,
  SENDER_PAGE_SIZE,
  SenderPatternDto,
  skippedMessage,
  SuggestionDto,
} from './review.models';
import { ReviewService } from './review.service';
import { SenderList } from './sender-list.component';

/**
 * `/review`: senders with suggestions on the left, the selected sender's groups on the right. Nothing
 * is updated optimistically: every action re-fetches the list, the detail and the sender's pattern.
 */
@Component({
  selector: 'app-review-page',
  imports: [
    ClaudeSenderActions,
    GroupCard,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTooltipModule,
    PageHeader,
    SenderList,
  ],
  templateUrl: './review-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .sender-title {
      font: var(--mat-sys-title-medium);
      overflow-wrap: anywhere;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewPage {
  private readonly review = inject(ReviewService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly editDialog = inject(REVIEW_EDIT_DIALOG);
  private readonly destroyRef = inject(DestroyRef);

  readonly senderPageSize = SENDER_PAGE_SIZE;
  readonly groupPageSize = GROUP_PAGE_SIZE;
  readonly maxAnalyse = MAX_ANALYSE_INDIVIDUALLY;

  readonly status = signal<ReviewStatus>('pending');
  private readonly search = signal('');
  private readonly senderPage = signal(1);
  readonly selected = signal<string | null>(null);
  private readonly groupPage = signal(1);
  /** Bumped after every action and reconnect: everything shown is re-fetched. */
  private readonly version = signal(0);

  readonly senders = signal<PagedDto<ReviewSenderDto> | null>(null);
  readonly sendersLoading = signal(false);
  readonly sendersFailed = signal(false);
  readonly detail = signal<ReviewSenderDetailDto | null>(null);
  readonly detailLoading = signal(false);
  readonly pattern = signal<SenderPatternDto | null>(null);
  /** Suggestion ids ticked for "Analyse individually". */
  readonly selection = signal<ReadonlySet<string>>(new Set());
  /** Ids the last group or bulk approve left pending. */
  readonly skipped = signal<ReadonlySet<string>>(new Set());
  readonly busy = signal(false);

  private readonly settings = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(catchError(() => of(null))),
    { initialValue: null },
  );
  readonly flagLabels = computed(() => {
    const s = this.settings();
    return s ? { action: s.actionLabelName, delete: s.deleteLabelName } : DEFAULT_FLAG_LABELS;
  });
  readonly claudeMode = computed(() => this.settings()?.claudeReviewerMode ?? 'off');

  /** The apply batch's job, from the hub or (after a reconnect) from the API, whichever is newer. */
  readonly applyJobId = signal<string | null>(null);
  private readonly fetchedJob = signal<JobDto | null>(null);
  /** Apply jobs already reported; the hub may still hold one as active until its next snapshot. */
  private readonly finishedJobIds = new Set<string>();
  readonly applyJob = computed(() => {
    const id = this.applyJobId();
    if (!id) return null;
    const fetched = this.fetchedJob();
    const held = fetched?.id === id ? fetched : null;
    const live = this.jobs.job(id);
    return live ? newerJob(live, held) : held;
  });
  readonly applyPercent = computed(() => progressPercent(this.applyJob()?.progress));

  readonly approvedCount = computed(() => this.detail()?.sender.approved ?? 0);
  readonly restPattern = computed(() => {
    const p = this.pattern();
    return canApplyRest(p) ? p : null;
  });

  constructor() {
    const sendersKey = computed(() => ({
      status: this.status(),
      search: this.search(),
      page: this.senderPage(),
      version: this.version(),
    }));
    toObservable(sendersKey)
      .pipe(
        tap(() => this.sendersLoading.set(true)),
        switchMap((k) =>
          this.review.listSenders(k.status, k.search, k.page, SENDER_PAGE_SIZE).pipe(orNull()),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.sendersLoading.set(false);
        this.sendersFailed.set(!page);
        this.senders.set(page);
        if (page && !this.selected() && page.items.length > 0)
          this.selected.set(page.items[0].address);
      });

    const detailKey = computed(() => ({
      address: this.selected(),
      status: this.status(),
      page: this.groupPage(),
      version: this.version(),
    }));
    toObservable(detailKey)
      .pipe(
        tap((k) => this.detailLoading.set(!!k.address)),
        switchMap((k) =>
          k.address
            ? this.review.sender(k.address, k.status, k.page, GROUP_PAGE_SIZE).pipe(orNull())
            : of(null),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((detail) => {
        this.detailLoading.set(false);
        this.detail.set(detail);
      });

    const patternKey = computed(() => ({ address: this.selected(), version: this.version() }));
    toObservable(patternKey)
      .pipe(
        tap(() => this.pattern.set(null)),
        switchMap((k) => (k.address ? this.review.pattern(k.address).pipe(orNull()) : of(null))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((pattern) => this.pattern.set(pattern));

    this.jobs.externalReviewChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((item) => this.onClaudeChange(item));

    // An apply job started elsewhere or before this page opened (the hub's snapshot, also after a
    // reconnect): follow it, so its progress and Cancel show and no second batch can be queued.
    effect(() => {
      const active = this.jobs
        .activeJobs()
        .find((j) => j.type === APPLY_JOB && !this.finishedJobIds.has(j.id));
      if (active && !this.applyJobId()) untracked(() => this.track(active.id));
    });
    // The apply job finished: show its outcome and re-fetch.
    effect(() => {
      const job = this.applyJob();
      if (job && !isActiveJob(job)) untracked(() => this.finishApply(job));
    });
    // Updates may have been missed while disconnected; the hub's snapshot holds active jobs only.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      untracked(() => {
        this.refresh();
        const id = this.applyJobId();
        if (id) {
          this.review
            .job(id)
            .pipe(orNull(), takeUntilDestroyed(this.destroyRef))
            .subscribe((job) => job && this.fetchedJob.set(job));
        }
      });
    });
  }

  onStatus(status: ReviewStatus): void {
    this.status.set(status);
    this.senderPage.set(1);
    this.resetDetail();
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
    this.resetDetail();
  }

  onGroupPage(event: PageEvent): void {
    this.groupPage.set(event.pageIndex + 1);
    this.selection.set(new Set());
  }

  approveMember(m: SuggestionDto): void {
    this.run(this.review.approve(m.id));
  }

  rejectMember(m: SuggestionDto): void {
    this.run(this.review.reject(m.id));
  }

  /** A message analysed on its own has no group key: its one member is decided directly. */
  approveGroup(group: ReviewGroupDto): void {
    const address = this.selected();
    if (!address) return;
    if (group.groupKey === null) {
      if (group.members[0]) this.approveMember(group.members[0]);
      return;
    }
    this.run(this.review.approveGroup(address, group.groupKey, outcomeOf(group)), (r) =>
      this.reportGroup('Approved', r),
    );
  }

  rejectGroup(group: ReviewGroupDto): void {
    const address = this.selected();
    if (!address) return;
    if (group.groupKey === null) {
      if (group.members[0]) this.rejectMember(group.members[0]);
      return;
    }
    this.run(this.review.rejectGroup(address, group.groupKey), (r) =>
      this.reportGroup('Rejected', r),
    );
  }

  editGroup(group: ReviewGroupDto): void {
    const address = this.selected();
    if (!address) return;
    this.afterEdit(this.editDialog.editGroup(address, group));
  }

  editMember(m: SuggestionDto): void {
    this.afterEdit(this.editDialog.editMember(m));
  }

  /** Patched in place; an accepted verdict changed suggestions, so everything is re-fetched. */
  onClaudeChange(item: ExternalReviewDto): void {
    const d = this.detail();
    if (!d) return;
    const patched = patchClaudeReview(d, item);
    this.detail.set(patched.detail);
    if (patched.accepted) this.refresh();
  }

  toggleMember(m: SuggestionDto): void {
    this.selection.update((current) => {
      const next = new Set(current);
      if (!next.delete(m.id)) next.add(m.id);
      return next;
    });
  }

  analyseIndividually(): void {
    const ids = [...this.selection()];
    if (ids.length === 0 || ids.length > MAX_ANALYSE_INDIVIDUALLY) return;
    this.run(this.review.analyseIndividually(ids), () => {
      this.selection.set(new Set());
      this.snackBar
        .open(
          `Analysing ${ids.length} ${ids.length === 1 ? 'message' : 'messages'} individually.`,
          'Analyse',
          {
            duration: 8000,
          },
        )
        .onAction()
        .subscribe(() => void this.router.navigate(['/analyse']));
    });
  }

  bulkApprove(): void {
    const limits = ANALYSIS_LIMITS.bulkApproveThreshold;
    openBulkApprove(this.dialog, {
      threshold: this.settings()?.bulkApproveThreshold ?? limits.max,
      min: limits.min,
      max: limits.max,
      step: limits.step,
      senderAddress: this.selected(),
    })
      .pipe(
        filter((changed) => changed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.refresh());
  }

  /** Applies the selected sender's approved suggestions; the API answers 409 when none is. */
  applyApproved(): void {
    const address = this.selected();
    if (!address || this.applyJobId()) return;
    this.run(this.review.apply(address), (batch) => this.track(batch.jobId));
  }

  applyRest(): void {
    const address = this.selected();
    const pattern = this.restPattern();
    if (!address || !pattern || this.applyJobId()) return;
    openConfirm(this.dialog, {
      title: 'Apply to rest of sender?',
      message: patternSummary(pattern, this.flagLabels()),
      confirm: 'Apply',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() =>
        this.run(this.review.applyRest(address), (r) => {
          this.track(r.batch?.jobId ?? null);
          const adjusted = r.protectedAdjusted
            ? `; ${r.protectedAdjusted} protected ${r.protectedAdjusted === 1 ? 'message is' : 'messages are'} not marked for deletion`
            : '';
          this.snackBar.open(
            `Created ${r.created} ${r.created === 1 ? 'suggestion' : 'suggestions'}${adjusted}. A Gmail filter from ${r.filterCandidate.from} can be created in Rules (M6).`,
            'Dismiss',
            { duration: 8000 },
          );
        }),
      );
  }

  /** The API refuses (409, shown by the error interceptor) while a chunk is pending. */
  cancelApply(): void {
    const id = this.applyJobId();
    if (!id) return;
    openConfirm(this.dialog, {
      title: 'Cancel apply?',
      message:
        'It stops at its next checkpoint. Messages already changed stay changed; undo them in History.',
      confirm: 'Cancel apply',
    })
      .pipe(
        filter((confirmed) => confirmed),
        switchMap(() => this.jobs.cancel(id).pipe(catchError(() => of(undefined)))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  private track(jobId: string | null): void {
    if (!jobId) return;
    this.fetchedJob.set(null);
    this.applyJobId.set(jobId);
  }

  private finishApply(job: JobDto): void {
    this.finishedJobIds.add(job.id);
    this.applyJobId.set(null);
    this.fetchedJob.set(null);
    this.refresh();
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Applied.')
        : job.status === 'cancelled'
          ? `Apply cancelled. ${job.progress?.message ?? ''}`.trim()
          : `Apply failed${job.error ? `: ${job.error}` : '.'}`;
    this.snackBar
      .open(message, 'History', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(['/history']));
  }

  private reportGroup(verb: string, r: GroupDecisionResponse): void {
    this.skipped.set(new Set(r.skipped));
    const changed = `${verb} ${r.changed} ${r.changed === 1 ? 'suggestion' : 'suggestions'}.`;
    this.snackBar.open(
      r.skipped.length ? `${changed} ${skippedMessage(r.skipped.length)}` : changed,
      'Dismiss',
      {
        duration: r.skipped.length ? 10_000 : 4000,
      },
    );
  }

  private afterEdit(saved: Observable<boolean>): void {
    saved
      .pipe(
        filter((s) => s),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.refresh());
  }

  /** Runs one action at a time, then re-fetches whatever happened (an error may mean the state moved). */
  private run<T>(request: Observable<T>, next?: (value: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.skipped.set(new Set());
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

  private resetDetail(): void {
    this.groupPage.set(1);
    this.selection.set(new Set());
    this.skipped.set(new Set());
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
