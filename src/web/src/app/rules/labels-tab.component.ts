import { DecimalPipe } from '@angular/common';
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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, concatMap, filter, map, Observable, of, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { ClaudeVerdict } from '../review/claude-verdict.component';
import { LabelDto } from '../review/labels.models';
import { LabelsService } from '../review/labels.service';
import { openApplyPlan } from './apply-plan-dialog.component';
import { LabelPlanItem } from './label-plan-item.component';
import { LabelPlanTree } from './label-plan-tree.component';
import {
  acceptedCounts,
  KIND_TITLES,
  LabelPlanDto,
  LabelPlanItemDto,
  PLAN_KINDS,
  TAXONOMY_PROPOSE_JOB,
  UpdatePlanItemRequest,
} from './label-plan.models';
import { RulesClaude } from './rules-claude.service';
import { RulesService } from './rules.service';

/** What the latest-plan request found: a plan, none (404) or an error. */
type Loaded = LabelPlanDto | 'none' | 'error';

/**
 * The Rules page's Labels tab: review the label plan or propose a taxonomy (a job), send the plan to Claude, edit and
 * accept items, apply them as a job, discard.
 */
@Component({
  selector: 'app-labels-tab',
  imports: [
    ClaudeVerdict,
    DecimalPipe,
    LabelPlanItem,
    LabelPlanTree,
    MatButtonModule,
    MatButtonToggleModule,
    MatDividerModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  providers: [RulesClaude],
  templateUrl: './labels-tab.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .banner {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LabelsTab {
  private readonly rules = inject(RulesService);
  private readonly labelsService = inject(LabelsService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  readonly claude = inject(RulesClaude);

  readonly plan = signal<LabelPlanDto | null>(null);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  /** Loaded, and no plan exists yet. */
  readonly none = signal(false);
  readonly labels = signal<readonly LabelDto[]>([]);
  readonly view = signal<'list' | 'tree'>('list');
  /** "Review labels", apply or discard in flight. */
  readonly working = signal(false);
  /** Item ids with an edit in flight. */
  readonly busy = signal<ReadonlySet<string>>(new Set());

  readonly kinds = PLAN_KINDS;
  readonly titles = KIND_TITLES;
  readonly draft = computed(() => this.plan()?.status === 'draft');
  readonly groups = computed(() => {
    const items = this.plan()?.items ?? [];
    return PLAN_KINDS.map((kind) => ({ kind, items: items.filter((i) => i.kind === kind) }));
  });
  readonly accepted = computed(
    () => this.plan()?.items.filter((i) => i.status === 'accepted').length ?? 0,
  );
  readonly failed = computed(
    () => this.plan()?.items.filter((i) => i.status === 'failed').length ?? 0,
  );
  /** The apply job while the plan is being applied. */
  readonly job = computed(() => {
    const plan = this.plan();
    return plan?.status === 'applying' && plan.jobId ? (this.jobs.job(plan.jobId) ?? null) : null;
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));
  readonly cancelling = signal(false);

  /** The taxonomy proposal this tab started or found running. */
  private readonly proposeJobId = signal<string | null>(null);
  readonly proposeJob = computed(() => {
    const id = this.proposeJobId();
    return id ? (this.jobs.job(id) ?? null) : null;
  });
  /** Queued or running (or the request in flight): no second proposal, review or apply meanwhile. */
  readonly proposing = computed(() => {
    const job = this.proposeJob();
    return this.proposeRequested() || (!!this.proposeJobId() && (!job || isActiveJob(job)));
  });
  readonly proposePercent = computed(() => progressPercent(this.proposeJob()?.progress));
  /** The last proposal's failure, until the next one starts. */
  readonly proposeError = signal<string | null>(null);
  private readonly proposeRequested = signal(false);
  /** The jobs service has held the followed proposal. */
  private proposeSeen = false;

  /** Each reload cancels the one in flight, so a stale plan never lands last. */
  private readonly reloads = new Subject<void>();
  /** Item edits run one at a time, so each response (the whole plan) includes every earlier edit. */
  private readonly edits = new Subject<{
    planId: string;
    itemId: string;
    request: UpdatePlanItemRequest;
  }>();
  /** Apply and taxonomy jobs whose end already reloaded the plan. */
  private readonly finishedJobIds = new Set<string>();

  readonly trackItem = (_: number, i: LabelPlanItemDto) => i.id;

  constructor() {
    this.claude.follow(() => ({ labelPlanId: this.plan()?.id }));
    this.reloads
      .pipe(
        switchMap(() => this.rules.latestPlan().pipe(catchError((e) => of(notFound(e))))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((loaded) => {
        this.loading.set(false);
        if (loaded === 'error') {
          this.loadFailed.set(true);
          return;
        }
        this.none.set(loaded === 'none');
        this.plan.set(loaded === 'none' ? null : loaded);
      });
    this.labelsService
      .labels()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (labels) => this.labels.set(labels), error: () => undefined });
    this.edits
      .pipe(
        concatMap(({ planId, itemId, request }) =>
          this.rules.updateItem(planId, itemId, request).pipe(
            map((updated): { itemId: string; updated: LabelPlanDto | null } => ({
              itemId,
              updated,
            })),
            catchError(() => of({ itemId, updated: null })),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ itemId, updated }) => {
        this.setBusy(itemId, false);
        // 400 / 409 show in a snackbar; the reload resets the item's controls to the stored state.
        if (updated) this.plan.set(updated);
        else this.reload();
      });

    // The apply job ended (completed, failed or cancelled): the plan shows each item's outcome.
    effect(() => {
      const job = this.job();
      if (!job || isActiveJob(job) || this.finishedJobIds.has(job.id)) return;
      this.finishedJobIds.add(job.id);
      untracked(() => {
        this.cancelling.set(false);
        this.reload();
        this.refreshLabels();
      });
    });
    // A proposal started elsewhere or before this tab opened: follow it.
    effect(() => {
      const active = this.jobs
        .activeJobs()
        .find((j) => j.type === TAXONOMY_PROPOSE_JOB && !this.finishedJobIds.has(j.id));
      if (active && !untracked(this.proposeJobId))
        untracked(() => this.proposeJobId.set(active.id));
    });
    // The proposal ended: a completed one stored a new draft, which opens in the tree. A job that
    // ended while the hub was disconnected drops out of the jobs service: the reload shows the result.
    effect(() => {
      const id = this.proposeJobId();
      const job = this.proposeJob();
      if (id && !job && this.proposeSeen) {
        this.proposeSeen = false;
        untracked(() => {
          this.proposeJobId.set(null);
          this.reload();
        });
        return;
      }
      if (job) this.proposeSeen = true;
      if (!job || isActiveJob(job) || this.finishedJobIds.has(job.id)) return;
      this.finishedJobIds.add(job.id);
      this.proposeSeen = false;
      untracked(() => {
        this.proposeJobId.set(null);
        if (job.status === 'completed') {
          this.view.set('tree');
          this.reload();
        } else if (job.status === 'failed') {
          this.proposeError.set(job.error ?? 'The taxonomy proposal failed.');
        }
      });
    });
    // Updates may have been missed while disconnected.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      untracked(() => this.reload());
    });
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.reloads.next();
  }

  /** Builds a new plan; an existing draft is discarded, so that asks first. */
  review(): void {
    if (this.working()) return;
    const confirmed: Observable<boolean> = this.draft()
      ? openConfirm(this.dialog, {
          title: 'Review the labels again?',
          message: 'The current draft plan and your decisions on it will be discarded.',
          confirm: 'Review labels',
        })
      : of(true);
    confirmed
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.working.set(true);
          return this.rules.createPlan();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (plan) => this.landed(plan),
        // The error interceptor shows the 502 / 503 reason.
        error: () => this.working.set(false),
      });
  }

  /**
   * Queues the taxonomy proposal; its new draft replaces the current one, so that asks first. The
   * error interceptor shows a 409 (no chat model, or a proposal already in progress).
   */
  proposeTaxonomy(): void {
    if (this.working() || this.proposing()) return;
    const confirmed: Observable<boolean> = this.draft()
      ? openConfirm(this.dialog, {
          title: 'Propose a taxonomy?',
          message: 'The proposal replaces the current draft plan and your decisions on it.',
          confirm: 'Propose taxonomy',
        })
      : of(true);
    confirmed
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.proposeRequested.set(true);
          this.proposeError.set(null);
          return this.rules.proposeTaxonomy();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (job) => {
          this.proposeJobId.set(job.id);
          this.proposeRequested.set(false);
        },
        error: () => this.proposeRequested.set(false),
      });
  }

  updateItem(item: LabelPlanItemDto, request: UpdatePlanItemRequest): void {
    const plan = this.plan();
    if (!plan || this.busy().has(item.id)) return;
    this.setBusy(item.id, true);
    this.edits.next({ planId: plan.id, itemId: item.id, request });
  }

  apply(): void {
    const plan = this.plan();
    if (!plan || this.working() || !this.accepted()) return;
    openApplyPlan(this.dialog, acceptedCounts(plan.items))
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.working.set(true);
          return this.rules.applyPlan(plan.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ jobId }) => {
          this.working.set(false);
          this.plan.set({ ...plan, status: 'applying', jobId });
          this.reload();
        },
        error: () => {
          this.working.set(false);
          this.reload();
        },
      });
  }

  /**
   * The job may refuse while a merge chunk is in flight (409, shown in a snackbar). The cancel
   * returns once the plan is back to draft, which the job's own cancelled event can precede.
   */
  cancelApply(): void {
    const job = this.job();
    if (!job || this.cancelling()) return;
    this.cancelling.set(true);
    this.jobs
      .cancel(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.cancelling.set(false);
          this.reload();
        },
        error: () => this.cancelling.set(false),
      });
  }

  discard(): void {
    const plan = this.plan();
    if (!plan || this.working()) return;
    openConfirm(this.dialog, {
      title: 'Discard this plan?',
      message: 'The plan and your decisions on it are dropped; Gmail is not changed.',
      confirm: 'Discard',
    })
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.working.set(true);
          return this.rules.discardPlan(plan.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.working.set(false);
          this.reload();
        },
        error: () => {
          this.working.set(false);
          this.reload();
        },
      });
  }

  isBusy(id: string): boolean {
    return this.busy().has(id);
  }

  private landed(plan: LabelPlanDto): void {
    this.working.set(false);
    this.none.set(false);
    this.loadFailed.set(false);
    this.plan.set(plan);
  }

  private refreshLabels(): void {
    this.labelsService
      .refresh()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (labels) => this.labels.set(labels), error: () => undefined });
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

function notFound(error: unknown): Loaded {
  return error instanceof HttpErrorResponse && error.status === 404 ? 'none' : 'error';
}
