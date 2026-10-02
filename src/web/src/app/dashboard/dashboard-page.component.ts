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
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { auditTime, catchError, filter, map, Observable, of, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { JobDto, JobStatus, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PageHeader } from '../layout/page-header';
import {
  dashboardControls,
  FetchStatusDto,
  fetchJobsKey,
  fetchProgressKey,
  fetchView,
  humanise,
} from './fetch.models';
import { FetchService } from './fetch.service';

/** Counts refetch at most this often while a fetch job runs. */
export const STATUS_REFRESH_MS = 5000;

/** `/dashboard`: mailbox fetch progress and controls, and the running jobs. */
@Component({
  selector: 'app-dashboard-page',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    PageHeader,
    RouterLink,
  ],
  templateUrl: './dashboard-page.component.html',
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .warning {
      color: var(--mat-sys-error);
    }
    .banner {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      border-radius: var(--mat-sys-corner-medium);
    }
    .banner a {
      color: inherit;
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
export class DashboardPage {
  private readonly fetch = inject(FetchService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  /** Every status request goes through here; `switchMap` drops an older response. */
  private readonly refresh = new Subject<number>();
  private requests = 0;
  /** The refresh `run()` asked for; only a response to it (or a later one) clears `busy`. */
  private runRequest: number | null = null;
  readonly jobs = inject(JobsService);

  readonly status = signal<FetchStatusDto | null>(null);
  readonly loadFailed = signal(false);
  /** A start or job control request is in flight. */
  readonly busy = signal(false);
  /** Pause or cancel requested per job id, with the status it had; shown until the status changes. */
  private readonly pending = signal<ReadonlyMap<string, PendingAction>>(new Map());

  readonly view = computed(() => {
    const status = this.status();
    if (!status) return null;
    const live = status.activeJob ? this.jobs.job(status.activeJob.id) : undefined;
    return fetchView(status, live);
  });
  readonly fetchPercent = computed(() => progressPercent(this.view()?.job?.progress));
  readonly runningJobs = computed(() => {
    const mismatch = this.status()?.accountMismatch ?? false;
    return this.jobs.activeJobs().map((job) => ({
      job,
      controls: dashboardControls(job, mismatch),
      percent: progressPercent(job.progress),
    }));
  });
  readonly reconnecting = computed(() => {
    const state = this.jobs.connectionState();
    return state === 'reconnecting' || state === 'disconnected';
  });

  /** Status refetches on a fetch job status or step change, or a reconnect. */
  private readonly refreshKey = computed(
    () => `${fetchJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.refresh
      .pipe(
        switchMap((id) =>
          this.fetch.getStatus().pipe(
            map((status) => ({ id, status })),
            catchError(() => of({ id, status: null })),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(({ id, status }) => {
        if (this.runRequest !== null && id >= this.runRequest) {
          this.runRequest = null;
          this.busy.set(false);
        }
        if (status) this.status.set(status);
        this.loadFailed.set(!status);
      });
    effect(() => {
      this.refreshKey();
      untracked(() => this.load());
    });
    // Counts move with every chunk: refetch at most every 5 s while a fetch job runs.
    toObservable(computed(() => fetchProgressKey(this.jobs.jobs())))
      .pipe(
        filter((key) => key !== ''),
        auditTime(STATUS_REFRESH_MS),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.load());
    // A pending pause or cancel ends when its job changes status (or goes).
    effect(() => {
      const pending = this.pending();
      if (pending.size === 0) return;
      const next = new Map([...pending].filter(([id, p]) => this.statusOf(id) === p.status));
      if (next.size !== pending.size) this.pending.set(next);
    });
  }

  load(): void {
    this.refresh.next(++this.requests);
  }

  start(): void {
    this.run(this.fetch.startMailboxFetch());
  }

  pause(job: JobDto): void {
    this.run(this.jobs.pause(job.id), () => this.markPending(job, 'pause'));
  }

  resume(job: JobDto): void {
    this.run(this.jobs.resume(job.id));
  }

  cancel(job: JobDto): void {
    openConfirm(this.dialog, {
      title: 'Cancel job?',
      message: "The job stops at its next checkpoint. A cancelled job can't be resumed.",
      confirm: 'Cancel job',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.run(this.jobs.cancel(job.id), () => this.markPending(job, 'cancel')));
  }

  /** `'pause'` or `'cancel'` while that request waits for the job's next checkpoint. */
  pendingAction(job: JobDto): PendingAction['action'] | null {
    const pending = this.pending().get(job.id);
    return pending && pending.status === job.status ? pending.action : null;
  }

  /** The job's status, or "Pausing…" / "Cancelling…" while a request waits. */
  statusText(job: JobDto): string {
    const action = this.pendingAction(job);
    if (action === 'pause') return 'Pausing…';
    if (action === 'cancel') return 'Cancelling…';
    return job.status;
  }

  jobLabel(job: JobDto): string {
    return humanise(job.type);
  }

  private statusOf(id: string): JobStatus | undefined {
    const shown = this.view()?.job;
    return (shown?.id === id ? shown : this.jobs.job(id))?.status;
  }

  private markPending(job: JobDto, action: PendingAction['action']): void {
    this.pending.update((current) => new Map(current).set(job.id, { action, status: job.status }));
  }

  /** Sends a request, then reloads the status; `busy` stays set until that status arrives. */
  private run(request: Observable<unknown>, done?: () => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        done?.();
        this.runRequest = ++this.requests;
        this.refresh.next(this.runRequest);
      },
      // The error interceptor already shows the message.
      error: () => this.busy.set(false),
    });
  }
}

interface PendingAction {
  action: 'pause' | 'cancel';
  status: JobStatus;
}
