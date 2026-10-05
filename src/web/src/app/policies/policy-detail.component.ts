import { DecimalPipe, PercentPipe } from '@angular/common';
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
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, filter, finalize, map, Observable, of, switchMap } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PageHeader } from '../layout/page-header';
import { fieldErrors } from '../review/edit-suggestion-dialog.component';
import { LabelsService } from '../review/labels.service';
import { documentTypeOptions } from '../review/review.models';
import { SettingsService } from '../settings/settings.service';
import { openApprovePolicy } from './approve-policy-dialog.component';
import {
  applyServerErrors,
  approveSummary,
  ApprovePolicyResponse,
  editRequest,
  label,
  policyForm,
  PolicyForm,
  SenderPolicyDetailDto,
} from './policies.models';
import { PoliciesService } from './policies.service';
import { PolicyFormComponent } from './policy-form.component';
import { PolicyRulesTable, RuleDecision } from './policy-rules-table.component';

/** `/policies/:id`: the sender profile, the policy and its rules; save, approve with apply progress, reject. */
@Component({
  selector: 'app-policy-detail',
  imports: [
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
    PageHeader,
    PercentPipe,
    PolicyFormComponent,
    PolicyRulesTable,
    RouterLink,
  ],
  templateUrl: './policy-detail.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
    .stat {
      padding: 0 0.5rem;
      border-radius: 9999px;
      font: var(--mat-sys-label-medium);
      line-height: 1.5rem;
      white-space: nowrap;
      background: var(--mat-sys-surface-container-highest);
    }
    th {
      text-align: start;
    }
    td,
    th {
      padding: 0.25rem 0.5rem;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      vertical-align: top;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PolicyDetail {
  private readonly policies = inject(PoliciesService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = toSignal(inject(ActivatedRoute).paramMap.pipe(map((p) => p.get('id') ?? '')), {
    initialValue: '',
  });
  readonly detail = signal<SenderPolicyDetailDto | null>(null);
  readonly form = signal<PolicyForm | null>(null);
  readonly loadError = signal<string | null>(null);
  /** A save, decision or approve request is in flight. */
  readonly busy = signal(false);
  /** Field errors no control takes, and refusals such as the 422 reason. */
  readonly formErrors = signal<string[]>([]);
  readonly actionError = signal<string | null>(null);
  /** The API said the saved changes reach the mail only after "Apply again". */
  readonly reapply = signal(false);
  readonly labels = signal<string[]>([]);
  readonly documentTypeParent = signal<string | null>(null);
  private readonly labelDtos = toSignal(
    inject(LabelsService)
      .labels()
      .pipe(catchError(() => of([]))),
    { initialValue: [] },
  );
  readonly documentTypes = computed(() => {
    const parent = this.documentTypeParent();
    return parent ? documentTypeOptions(this.labelDtos(), parent).map((t) => `${parent}/${t}`) : [];
  });
  readonly labelNames = computed(() =>
    this.labelDtos()
      .filter((l) => l.type === 'user')
      .map((l) => l.name)
      .sort((a, b) => a.localeCompare(b)),
  );
  /** Pristine, touched, value and status changes of the current form. */
  private readonly formChanges = toSignal(
    toObservable(this.form).pipe(switchMap((f) => (f ? f.events : of(null)))),
  );
  readonly dirty = computed(() => {
    this.formChanges();
    return this.form()?.dirty ?? false;
  });

  /** The apply job being followed, from the hub or the API answer, whichever is newer. */
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
  readonly label = label;

  constructor() {
    inject(SettingsService)
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.documentTypeParent.set(s.documentTypeParent ?? null),
        // The error interceptor shows why; the form works without the document-type field.
        error: () => undefined,
      });
    effect(() => {
      const id = this.id();
      this.jobs.reconnects();
      if (id) untracked(() => this.load(id));
    });
    effect(() => {
      const job = this.job();
      if (job && !isActiveJob(job)) untracked(() => this.finish(job));
    });
    // Updates may have been missed while disconnected, and the hub's snapshot holds active jobs only:
    // the followed apply job's REST status ends it as above.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      untracked(() => this.checkJob());
    });
  }

  /** Reloads the policy; unsaved edits are kept unless `reset`. */
  load(id = this.id(), reset = false): void {
    this.policies
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => this.show(detail, reset),
        error: (error: unknown) => this.loadError.set(problemText(error)),
      });
  }

  discard(): void {
    const detail = this.detail();
    if (detail) this.show(detail, true);
  }

  save(): void {
    const form = this.form();
    if (!form || this.busy()) return;
    if (form.invalid) {
      form.markAllAsTouched();
      return;
    }
    this.formErrors.set([]);
    this.run(this.policies.edit(this.id(), editRequest(form)), {
      next: (response) => {
        this.show(response.policy, true);
        this.reapply.set(response.reapply);
        this.snackBar.open(
          response.reapply ? 'Saved. Apply again to update past mail.' : 'Saved.',
          undefined,
          {
            duration: 4000,
          },
        );
      },
      error: (error) => {
        if (!(error instanceof HttpErrorResponse) || error.status !== 400) return;
        this.formErrors.set(applyServerErrors(form, fieldErrors(error)));
      },
    });
  }

  decideRule({ ruleId, decision }: RuleDecision): void {
    this.run(this.policies.decideRule(this.id(), ruleId, decision), {
      next: (detail) => this.show(detail, true),
    });
  }

  approve(): void {
    const detail = this.detail();
    if (!detail || this.busy()) return;
    openApprovePolicy(this.dialog, {
      name: detail.policy.displayName || detail.policy.scopeKey,
      summary: approveSummary(detail),
    })
      .pipe(filter(Boolean), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.startApply(this.policies.approve(this.id())));
  }

  applyAgain(): void {
    this.startApply(this.policies.apply(this.id()));
  }

  reject(): void {
    this.run(this.policies.reject(this.id()), {
      next: () => {
        this.snackBar.open('Policy rejected.', undefined, { duration: 4000 });
        this.load(this.id(), true);
      },
    });
  }

  /** 409 (already approved, or a job running) is shown by the error interceptor; 422 here with its reason. */
  private startApply(request: Observable<ApprovePolicyResponse>): void {
    this.actionError.set(null);
    this.run(request, {
      next: (response) => {
        this.reapply.set(false);
        this.load(this.id(), true);
        if (response.jobId) this.trackedJobId.set(response.jobId);
      },
      error: (error) => {
        if (error instanceof HttpErrorResponse && error.status === 422)
          this.actionError.set(problemText(error));
      },
    });
  }

  private run<T>(
    request: Observable<T>,
    handlers: { next: (value: T) => void; error?: (error: unknown) => void },
  ): void {
    this.busy.set(true);
    request
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({ next: handlers.next, error: (e: unknown) => handlers.error?.(e) });
  }

  private show(detail: SenderPolicyDetailDto, reset: boolean): void {
    this.loadError.set(null);
    this.detail.set(detail);
    if (reset || !this.form()?.dirty) {
      const form = policyForm(detail);
      // The API edits proposed and approved policies only.
      if (detail.policy.status === 'rejected') form.disable();
      this.form.set(form);
      this.formErrors.set([]);
    }
  }

  /** Re-reads the followed apply job over REST; unreadable stops following. */
  private checkJob(): void {
    const id = this.trackedJobId();
    if (!id) return;
    this.jobs
      .fetch(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (job) => {
          if (this.trackedJobId() === id) this.startedJob.set(job);
        },
        error: () => {
          if (this.trackedJobId() !== id) return;
          this.finishedJobIds.add(id);
          this.trackedJobId.set(null);
          this.startedJob.set(null);
        },
      });
  }

  private finish(job: JobDto): void {
    if (this.finishedJobIds.has(job.id)) return;
    this.finishedJobIds.add(job.id);
    this.trackedJobId.set(null);
    this.startedJob.set(null);
    this.load(this.id());
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Policy applied.')
        : job.status === 'cancelled'
          ? `Apply cancelled. ${job.progress?.message ?? ''}`.trim()
          : `Apply failed${job.error ? `: ${job.error}` : '.'}`;
    this.snackBar
      .open(message, 'History', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(['/history']));
  }
}

/** A load or action error: the ProblemDetails title and detail. */
function problemText(error: unknown): string {
  return error instanceof HttpErrorResponse ? errorMessage(error) : 'Something went wrong.';
}
