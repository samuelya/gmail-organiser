import { DestroyRef, inject, Signal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { TOP_SENDERS } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { JobsService } from '../core/jobs.service';

/** "Propose policy" for one sender: in-flight addresses and the action itself. */
export interface ProposePolicy {
  readonly starting: Signal<ReadonlySet<string>>;
  start(address: string): void;
}

/**
 * Starts a top senders run for one sender (count 1), which proposes a policy for it (replacing a rejected one),
 * then opens Analyse. The run's job is read over REST so a hub event missed across a reconnect still shows.
 */
export function injectProposePolicy(): ProposePolicy {
  const analysis = inject(AnalysisService);
  const jobs = inject(JobsService);
  const router = inject(Router);
  const snackBar = inject(MatSnackBar);
  const destroyRef = inject(DestroyRef);
  const starting = signal<ReadonlySet<string>>(new Set());
  const done = (address: string) =>
    starting.update((busy) => new Set([...busy].filter((a) => a !== address)));

  return {
    starting: starting.asReadonly(),
    start(address: string): void {
      if (starting().has(address)) return;
      starting.update((busy) => new Set(busy).add(address));
      analysis
        .start({ scope: TOP_SENDERS, senderAddress: address, count: 1 })
        .pipe(takeUntilDestroyed(destroyRef))
        .subscribe({
          next: (run) => {
            done(address);
            if (run.jobId) jobs.fetch(run.jobId).subscribe({ error: () => undefined });
            snackBar.open(`Proposing a policy for ${address}.`, 'Dismiss', { duration: 6000 });
            void router.navigate(['/analyse']);
          },
          // The error interceptor shows the server's problem detail.
          error: () => done(address),
        });
    },
  };
}
