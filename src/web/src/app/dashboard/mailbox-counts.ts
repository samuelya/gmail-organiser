import { computed, DestroyRef, effect, inject, Signal, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, Observable, of, Subject, switchMap } from 'rxjs';
import { analysisJobsKey } from '../analyse/analysis.models';
import { JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { fetchJobStatusKey } from './fetch.models';

/** Changes when an analysis or fetch job appears or changes status: mailbox counts may have changed. */
export function mailboxCountsKey(jobs: readonly JobDto[]): string {
  return `${analysisJobsKey(jobs)}|${fetchJobStatusKey(jobs)}`;
}

/** The latest result of a GET of mailbox counts. */
export interface MailboxCounts<T> {
  /** The last result that loaded; kept after a failed reload. */
  readonly value: Signal<T | null>;
  /** The latest request failed. */
  readonly loadFailed: Signal<boolean>;
  /** Fetches again, dropping any request still in flight. */
  load(): void;
}

/**
 * Runs `fetch` now, again on every `mailboxCountsKey` change and after a reconnect, and on `load()`.
 * Call in an injection context; requests stop when that context is destroyed.
 */
export function mailboxCounts<T>(fetch: () => Observable<T>): MailboxCounts<T> {
  const jobs = inject(JobsService);
  const refresh = new Subject<void>();
  const value = signal<T | null>(null);
  const loadFailed = signal(false);
  const load = () => {
    loadFailed.set(false);
    refresh.next();
  };

  refresh
    .pipe(
      switchMap(() => fetch().pipe(catchError(() => of(null)))),
      takeUntilDestroyed(inject(DestroyRef)),
    )
    .subscribe((result) => {
      if (result) value.set(result);
      loadFailed.set(!result);
    });
  const key = computed(() => `${mailboxCountsKey(jobs.jobs())}|${jobs.reconnects()}`);
  effect(() => {
    key();
    untracked(load);
  });

  return { value: value.asReadonly(), loadFailed: loadFailed.asReadonly(), load };
}
