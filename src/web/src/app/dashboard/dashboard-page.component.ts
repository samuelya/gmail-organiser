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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { filter, Observable, Subscription, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { JobDto, jobControls, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PageHeader } from '../layout/page-header';
import { FetchStatusDto, fetchJobsKey, fetchView } from './fetch.models';
import { FetchService } from './fetch.service';

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
  private loadSub: Subscription | null = null;
  readonly jobs = inject(JobsService);

  readonly status = signal<FetchStatusDto | null>(null);
  readonly loadFailed = signal(false);
  /** A start or job control request is in flight. */
  readonly busy = signal(false);

  readonly view = computed(() => {
    const status = this.status();
    if (!status) return null;
    const live = status.activeJob ? this.jobs.job(status.activeJob.id) : undefined;
    return fetchView(status, live);
  });
  readonly fetchPercent = computed(() => progressPercent(this.view()?.job?.progress));
  readonly runningJobs = computed(() =>
    this.jobs.activeJobs().map((job) => ({
      job,
      controls: jobControls(job.status),
      percent: progressPercent(job.progress),
    })),
  );
  readonly reconnecting = computed(() => {
    const state = this.jobs.connectionState();
    return state === 'reconnecting' || state === 'disconnected';
  });

  /** Status refetches on a fetch job's status change or a reconnect, not on progress ticks. */
  private readonly refreshKey = computed(
    () => `${fetchJobsKey(this.jobs.jobs())}|${this.jobs.reconnects()}`,
  );

  constructor() {
    this.destroyRef.onDestroy(() => this.loadSub?.unsubscribe());
    effect(() => {
      this.refreshKey();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loadSub?.unsubscribe();
    this.loadSub = this.fetch.getStatus().subscribe({
      next: (status) => {
        this.status.set(status);
        this.loadFailed.set(false);
      },
      error: () => this.loadFailed.set(true),
    });
  }

  start(): void {
    this.run(this.fetch.startMailboxFetch());
  }

  pause(job: JobDto): void {
    this.run(this.jobs.pause(job.id));
  }

  resume(job: JobDto): void {
    this.run(this.jobs.resume(job.id));
  }

  cancel(job: JobDto): void {
    openConfirm(this.dialog, {
      title: 'Cancel job?',
      message:
        "The job stops at its next checkpoint. A cancelled fetch can't be resumed; starting again begins a new fetch.",
      confirm: 'Cancel job',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.run(this.jobs.cancel(job.id)));
  }

  jobLabel(job: JobDto): string {
    return job.type.replaceAll('_', ' ');
  }

  private run(request: Observable<unknown>): void {
    if (this.busy()) return;
    this.busy.set(true);
    request
      .pipe(
        switchMap(() => this.fetch.getStatus()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (status) => {
          this.busy.set(false);
          this.status.set(status);
        },
        // The error interceptor already shows the message.
        error: () => this.busy.set(false),
      });
  }
}
