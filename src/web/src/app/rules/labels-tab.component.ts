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
import { catchError, filter, Observable, of, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
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
  UpdatePlanItemRequest,
} from './label-plan.models';
import { RulesService } from './rules.service';

/** What the latest-plan request found: a plan, none (404) or an error. */
type Loaded = LabelPlanDto | 'none' | 'error';

/** The Rules page's Labels tab: review the label plan, edit and accept items, apply them as a job, discard. */
@Component({
  selector: 'app-labels-tab',
  imports: [
    DecimalPipe,
    LabelPlanItem,
    LabelPlanTree,
    MatButtonModule,
    MatButtonToggleModule,
    MatDividerModule,
    MatIconModule,
    MatProgressBarModule,
  ],
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
  readonly accepted = computed(() => this.plan()?.items.filter((i) => i.status === 'accepted').length ?? 0);
  readonly failed = computed(() => this.plan()?.items.filter((i) => i.status === 'failed').length ?? 0);
  /** The apply job while the plan is being applied. */
  readonly job = computed(() => {
    const plan = this.plan();
    return plan?.status === 'applying' && plan.jobId ? (this.jobs.job(plan.jobId) ?? null) : null;
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));
  readonly cancelling = signal(false);

  /** Each reload cancels the one in flight, so a stale plan never lands last. */
  private readonly reloads = new Subject<void>();
  /** Apply jobs whose end already reloaded the plan. */
  private readonly finishedJobIds = new Set<string>();

  readonly trackItem = (_: number, i: LabelPlanItemDto) => i.id;

  constructor() {
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

  updateItem(item: LabelPlanItemDto, request: UpdatePlanItemRequest): void {
    const plan = this.plan();
    if (!plan || this.busy().has(item.id)) return;
    this.setBusy(item.id, true);
    this.rules
      .updateItem(plan.id, item.id, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updated) => {
          this.setBusy(item.id, false);
          this.plan.set(updated);
        },
        // 400 / 409 show in a snackbar; the reload resets the item's controls to the stored state.
        error: () => {
          this.setBusy(item.id, false);
          this.reload();
        },
      });
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

  /** The job may refuse while a merge chunk is in flight (409, shown in a snackbar). */
  cancelApply(): void {
    const job = this.job();
    if (!job || this.cancelling()) return;
    this.cancelling.set(true);
    this.jobs
      .cancel(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ error: () => this.cancelling.set(false) });
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
