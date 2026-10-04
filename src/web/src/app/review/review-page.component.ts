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
import { catchError, filter, finalize, map, Observable, of, switchMap, tap } from 'rxjs';
import { analysisJobsKey } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { ExternalReviewDto } from '../core/claude.models';
import { openConfirm } from '../core/confirm-dialog';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { ANALYSIS_LIMITS } from '../settings/settings.models';
import { SettingsService } from '../settings/settings.service';
import {
  AlternativeDecision,
  alternativeMessage,
  alternativeRequest,
  AlternativeTarget,
  PENDING_AGAIN_NOTE,
  repends,
} from './alternative.models';
import { ApplyTracker } from './apply-tracker';
import { openBulkApprove } from './bulk-approve-dialog.component';
import { ClaudeSenderActions } from './claude-verdict.component';
import { GroupCard } from './group-card.component';
import {
  applyRestRequest,
  canApplyRest,
  DEFAULT_FLAG_LABELS,
  GROUP_PAGE_SIZE,
  GroupDecisionResponse,
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
import { SelectionActions } from './selection-actions.component';
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
    SelectionActions,
    SenderList,
  ],
  providers: [ApplyTracker],
  templateUrl: './review-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .note {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      border-radius: var(--mat-sys-corner-small);
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
  private readonly editDialog = inject(REVIEW_EDIT_DIALOG);
  private readonly destroyRef = inject(DestroyRef);

  readonly senderPageSize = SENDER_PAGE_SIZE;
  readonly groupPageSize = GROUP_PAGE_SIZE;
  readonly pendingAgainNote = PENDING_AGAIN_NOTE;

  readonly status = signal<ReviewStatus>('pending');
  /** The "Re-analysed" filter: only suggestions with a re-analysis result waiting. */
  readonly reanalysed = signal(false);
  private readonly search = signal('');
  private readonly senderPage = signal(1);
  readonly selected = signal<string | null>(null);
  private readonly groupPage = signal(1);
  /** The status and "Re-analysed" filter of the last senders list shown. */
  private readonly listedFilter = signal<string | null>(null);
  /** Bumped after every action and reconnect: everything shown is re-fetched. */
  private readonly version = signal(0);

  readonly senders = signal<PagedDto<ReviewSenderDto> | null>(null);
  readonly sendersLoading = signal(false);
  readonly sendersFailed = signal(false);
  readonly detail = signal<ReviewSenderDetailDto | null>(null);
  readonly detailLoading = signal(false);
  readonly pattern = signal<SenderPatternDto | null>(null);
  /** Suggestion ids ticked for "Analyse individually" and "Re-analyse". */
  readonly selection = signal<ReadonlySet<string>>(new Set());
  readonly selectionHasApplied = computed(() => {
    const ids = this.selection();
    return !!this.detail()?.groups.some((g) =>
      g.members.some((m) => m.status === 'applied' && ids.has(m.id)),
    );
  });
  /** Suggestions with a re-analysis result waiting, from the summary; null until loaded. */
  readonly alternatives = signal<number | null>(null);
  /** "Use new" re-pended an approved or applied suggestion. */
  readonly pendingAgain = signal(false);
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

  /** The apply batch's job and its outcome. */
  readonly apply = inject(ApplyTracker);

  readonly approvedCount = computed(() => this.detail()?.sender.approved ?? 0);
  readonly restPattern = computed(() => {
    const p = this.pattern();
    return canApplyRest(p) ? p : null;
  });

  constructor() {
    const sendersKey = computed(() => ({
      status: this.status(),
      reanalysed: this.reanalysed(),
      search: this.search(),
      page: this.senderPage(),
      version: this.version(),
    }));
    toObservable(sendersKey)
      .pipe(
        tap(() => this.sendersLoading.set(true)),
        switchMap((k) =>
          this.review.listSenders(k.status, k.search, k.page, SENDER_PAGE_SIZE, k.reanalysed).pipe(
            orNull(),
            map((page) => ({ listed: `${k.status}|${k.reanalysed}`, page })),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ listed, page }) => {
        this.sendersLoading.set(false);
        this.sendersFailed.set(!page);
        this.senders.set(page);
        if (!page) return;
        // A tab or "Re-analysed" change moves to the new list's first sender unless the selected one is in it.
        const selected = this.selected();
        const changed = listed !== this.listedFilter();
        this.listedFilter.set(listed);
        if (!selected || (changed && !page.items.some((s) => s.address === selected)))
          this.selected.set(page.items[0]?.address ?? null);
      });

    // Waits for the list of the current tab and filter, so a sender it hides is never requested (404).
    const detailKey = computed(() => ({
      listed: this.listedFilter() === `${this.status()}|${this.reanalysed()}`,
      address: this.selected(),
      status: this.status(),
      reanalysed: this.reanalysed(),
      page: this.groupPage(),
      version: this.version(),
    }));
    toObservable(detailKey)
      .pipe(
        filter((k) => k.listed),
        tap((k) => this.detailLoading.set(!!k.address)),
        switchMap((k) =>
          k.address
            ? this.review
                .sender(k.address, k.status, k.page, GROUP_PAGE_SIZE, k.reanalysed)
                .pipe(orNull())
            : of(null),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((detail) => {
        this.detailLoading.set(false);
        this.detail.set(detail);
        this.pruneSelection(detail);
      });

    const patternKey = computed(() => ({ address: this.selected(), version: this.version() }));
    toObservable(patternKey)
      .pipe(
        tap(() => this.pattern.set(null)),
        switchMap((k) => (k.address ? this.review.pattern(k.address).pipe(orNull()) : of(null))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((pattern) => this.pattern.set(pattern));

    // The count changes with every decision and whenever an analysis run starts or ends.
    const analysis = inject(AnalysisService);
    const summaryKey = computed(
      () => `${this.version()}|${analysisJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
    );
    toObservable(summaryKey)
      .pipe(
        switchMap(() => analysis.summary().pipe(orNull())),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((summary) => summary && this.alternatives.set(summary.alternatives));

    this.jobs.externalReviewChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((item) => this.onClaudeChange(item));

    this.apply.finished.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.refresh());
    // Updates may have been missed while disconnected.
    effect(() => {
      if (this.jobs.reconnects() > 0) untracked(() => this.refresh());
    });
  }

  onStatus(status: ReviewStatus): void {
    this.status.set(status);
    this.senderPage.set(1);
    this.resetDetail();
  }

  onReanalysed(on: boolean): void {
    this.reanalysed.set(on);
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

  /** A run was queued for the ticked members. */
  onSelectionStarted(): void {
    this.selection.set(new Set());
    this.refresh();
  }

  /** "Use new" (accept) or "Keep current" (discard) for a member or a whole group card. */
  decideAlternative(decision: AlternativeDecision, target: AlternativeTarget): void {
    const address = this.selected();
    if (!address) return;
    const request = alternativeRequest(target, address, this.status());
    const call =
      decision === 'accept'
        ? this.review.acceptAlternatives(request)
        : this.review.discardAlternatives(request);
    this.run(call, (r) => {
      if (decision === 'accept' && r.accepted > 0 && repends(target)) this.pendingAgain.set(true);
      this.snackBar.open(alternativeMessage(decision, r), 'Dismiss', { duration: 6000 });
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
    if (!address || this.apply.jobId()) return;
    this.run(this.review.apply(address), (batch) => this.apply.track(batch.jobId));
  }

  applyRest(): void {
    const address = this.selected();
    const pattern = this.restPattern();
    if (!address || !pattern || this.apply.jobId()) return;
    const parent = this.settings()?.documentTypeParent ?? null;
    const request = applyRestRequest(pattern, parent);
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
        this.run(this.review.applyRest(address, request), (r) => {
          const jobId = r.batch?.jobId ?? null;
          this.apply.track(jobId);
          const adjusted = r.protectedAdjusted
            ? `; ${r.protectedAdjusted} protected ${r.protectedAdjusted === 1 ? 'message is' : 'messages are'} not marked for deletion`
            : '';
          const created = `Created ${r.created} ${r.created === 1 ? 'suggestion' : 'suggestions'}${adjusted}.`;
          // The apply job's completion snackbar replaces this one, so the offer moves there.
          if (jobId) {
            this.apply.offerFilterAfter(jobId, r.filterCandidate.from);
            this.snackBar.open(created, 'Dismiss', { duration: 10_000 });
          } else {
            this.apply.offerFilter(created, r.filterCandidate.from);
          }
        }),
      );
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

  /** Ticks only survive a reload on members still shown: an applied or moved member drops out. */
  private pruneSelection(detail: ReviewSenderDetailDto | null): void {
    const current = this.selection();
    if (current.size === 0) return;
    const shown = new Set(detail?.groups.flatMap((g) => g.members.map((m) => m.id)) ?? []);
    const kept = [...current].filter((id) => shown.has(id));
    if (kept.length !== current.size) this.selection.set(new Set(kept));
  }

  private resetDetail(): void {
    this.pendingAgain.set(false);
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
