import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { catchError, filter, of, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { APPLY_JOB } from './review.models';
import { ReviewService } from './review.service';

/**
 * The Review page's apply batch job, from the hub or (after a reconnect) from the API, whichever is newer:
 * its progress, Cancel and the outcome snackbar. Provided by the page, so it lives as long as the page.
 */
@Injectable()
export class ApplyTracker {
  private readonly jobs = inject(JobsService);
  private readonly review = inject(ReviewService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly jobId = signal<string | null>(null);
  private readonly fetchedJob = signal<JobDto | null>(null);
  /** Apply jobs already reported; the hub may still hold one as active until its next snapshot. */
  private readonly finishedJobIds = new Set<string>();
  /** Apply-to-rest jobs whose completion snackbar offers "Create filter" for this sender. */
  private readonly filterOffers = new Map<string, string>();
  private readonly finishedJobs = new Subject<JobDto>();
  /** An apply job ended (completed, failed or cancelled): what is shown changed. */
  readonly finished = this.finishedJobs.asObservable();

  readonly job = computed(() => {
    const id = this.jobId();
    if (!id) return null;
    const fetched = this.fetchedJob();
    const held = fetched?.id === id ? fetched : null;
    const live = this.jobs.job(id);
    return live ? newerJob(live, held) : held;
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));

  constructor() {
    // An apply job started elsewhere or before this page opened (the hub's snapshot, also after a
    // reconnect): follow it, so its progress and Cancel show and no second batch can be queued.
    effect(() => {
      const active = this.jobs
        .activeJobs()
        .find((j) => j.type === APPLY_JOB && !this.finishedJobIds.has(j.id));
      if (active && !this.jobId()) untracked(() => this.track(active.id));
    });
    // The apply job finished: show its outcome.
    effect(() => {
      const job = this.job();
      if (job && !isActiveJob(job)) untracked(() => this.finish(job));
    });
    // Updates may have been missed while disconnected; the hub's snapshot holds active jobs only.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      const id = untracked(() => this.jobId());
      if (!id) return;
      untracked(() =>
        this.review
          .job(id)
          .pipe(
            catchError(() => of(null)),
            takeUntilDestroyed(this.destroyRef),
          )
          .subscribe((job) => job && this.fetchedJob.set(job)),
      );
    });
  }

  track(jobId: string | null): void {
    if (!jobId) return;
    this.fetchedJob.set(null);
    this.jobId.set(jobId);
  }

  /** Once the job completes, its snackbar offers "Create filter" for `from`. */
  offerFilterAfter(jobId: string, from: string): void {
    this.filterOffers.set(jobId, from);
  }

  offerFilter(message: string, from: string): void {
    this.snackBar
      .open(message, 'Create filter', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(['/rules'], { queryParams: { propose: from } }));
  }

  /** The API refuses (409, shown by the error interceptor) while a chunk is pending. */
  cancel(): void {
    const id = this.jobId();
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

  private finish(job: JobDto): void {
    this.finishedJobIds.add(job.id);
    this.jobId.set(null);
    this.fetchedJob.set(null);
    this.finishedJobs.next(job);
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Applied.')
        : job.status === 'cancelled'
          ? `Apply cancelled. ${job.progress?.message ?? ''}`.trim()
          : `Apply failed${job.error ? `: ${job.error}` : '.'}`;
    const offer = this.filterOffers.get(job.id);
    this.filterOffers.delete(job.id);
    if (offer && job.status === 'completed') {
      this.offerFilter(message, offer);
      return;
    }
    this.snackBar
      .open(message, 'History', { duration: 10_000 })
      .onAction()
      .subscribe(() => void this.router.navigate(['/history']));
  }
}
