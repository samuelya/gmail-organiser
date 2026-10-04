import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { filter, Observable, Subject, switchMap } from 'rxjs';
import { AnalysisRunDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { injectAnalysisRunActive, MAX_COMPARE, openCompareConfirm } from '../analyse/compare-run';
import { isActiveJob } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { ReviewGroupDto, SuggestionDto } from './review.models';

/** Tooltip of a card's "Re-analyse" when it holds more members than one re-analysis takes. */
export const CARD_TOO_LARGE_TOOLTIP = `At most ${MAX_COMPARE} at a time`;

/**
 * The per-member and per-card "Re-analyse" of the Review page: starts a re-analysis (compare run) and
 * follows its job, so the originating button spins until the run ends. Provided by the page.
 */
@Injectable()
export class CardReanalyse {
  private readonly analysis = inject(AnalysisService);
  private readonly jobs = inject(JobsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly runActive = injectAnalysisRunActive();

  /** Suggestion ids of the re-analysis started here, until its run ends. */
  readonly ids = signal<ReadonlySet<string>>(new Set());
  /** The re-analysis was started from a whole card (its spinner), not from one member. */
  readonly fromCard = signal(false);
  private readonly starting = signal(false);
  private readonly jobId = signal<string | null>(null);
  /** The job was seen queued or running: once the hub drops it, the run is over. */
  private seen = false;
  private readonly ended = new Subject<void>();
  /** The run started here ended: what is shown changed. */
  readonly finished = this.ended.asObservable();

  /** Any analysis run (normal or re-analysis) is queued or running, or one is being started here. */
  readonly blocked = computed(() => this.runActive() || this.starting() || this.ids().size > 0);

  constructor() {
    effect(() => {
      const id = this.jobId();
      if (!id) return;
      const job = this.jobs.job(id);
      if (job && isActiveJob(job)) this.seen = true;
      else if (job || this.seen) untracked(() => this.finish());
    });
    // Updates may have been missed while disconnected; the hub's snapshot holds active jobs only.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      untracked(() => {
        const id = this.jobId();
        const job = id ? this.jobs.job(id) : undefined;
        if (id && !(job && isActiveJob(job))) this.finish();
      });
    });
  }

  /** One email: no confirm. */
  member(m: SuggestionDto): void {
    this.start([m.id], false, () => this.analysis.startCompareRun({ suggestionIds: [m.id] }));
  }

  /** Every loaded member of the card, any status, after a confirm. */
  card(group: ReviewGroupDto): void {
    const ids = group.members.map((m) => m.id);
    if (ids.length === 0 || ids.length > MAX_COMPARE) return;
    const note = group.truncated
      ? `Only the newest ${ids.length.toLocaleString()} of ${group.size.toLocaleString()} members shown are re-analysed.`
      : undefined;
    const request = () =>
      openCompareConfirm(this.dialog, ids.length, false, note).pipe(
        filter((confirmed) => confirmed),
        switchMap(() => this.analysis.startCompareRun({ suggestionIds: ids })),
      );
    this.start(ids, true, request);
  }

  private start(ids: string[], fromCard: boolean, request: () => Observable<AnalysisRunDto>): void {
    if (this.blocked()) return;
    this.starting.set(true);
    request()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (run) => {
          this.starting.set(false);
          this.ids.set(new Set(ids));
          this.fromCard.set(fromCard);
          this.seen = false;
          this.jobId.set(run.jobId);
          const n = ids.length;
          this.snackBar.open(
            `Re-analysing ${n} ${n === 1 ? 'message' : 'messages'}; compare the results here once it is done.`,
            'Dismiss',
            { duration: 6000 },
          );
          if (!run.jobId) this.finish();
        },
        // The error interceptor shows the server's problem detail (a 400 or 409 among them).
        error: () => this.starting.set(false),
        complete: () => this.starting.set(false),
      });
  }

  private finish(): void {
    this.jobId.set(null);
    this.ids.set(new Set());
    this.fromCard.set(false);
    this.seen = false;
    this.ended.next();
  }
}
