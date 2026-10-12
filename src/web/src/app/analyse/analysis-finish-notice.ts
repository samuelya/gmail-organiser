import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, inject, Injectable } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatSnackBar, MatSnackBarRef, TextOnlySnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { AnalysisRunDto, countText, reviewSuggestionsParams } from './analysis.models';
import { AnalysisService } from './analysis.service';

export const FINISH_NOTICE_MS = 8000;

interface Notice {
  message: string;
  action?: string;
  /** Where the action goes: a URL, so it works however the page changed in the meantime. */
  link?: { path: string; queryParams: Record<string, string> };
}

/** What an analyse run that ended says: completed links to its suggestions, failed to the failed runs. */
export function noticeOf(run: AnalysisRunDto): Notice | null {
  if (run.kind !== 'analyse') return null;
  if (run.status === 'failed') {
    return {
      message: 'Analysis failed.',
      action: 'Details',
      link: { path: '/analyse', queryParams: { status: 'failed' } },
    };
  }
  if (run.status !== 'completed') return null;
  if (run.messagesCovered <= 0) return { message: 'Analysis finished: no messages covered.' };
  const params = reviewSuggestionsParams(run);
  return {
    message: `Analysis finished: ${countText(run.messagesCovered, 'message', 'messages')} covered.`,
    ...(params && { action: 'Review', link: { path: '/review', queryParams: params } }),
  };
}

/**
 * Finish notices for the Analyse page (provided by it, so it ends with the page). A run listed as active during the
 * visit is watched until it is found finished: in the finished list, or by id once it has left a complete queue. Its
 * notice is queued, so several runs ending together each get theirs.
 */
@Injectable()
export class AnalysisFinishNotice {
  private readonly analysis = inject(AnalysisService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  /** Runs seen active on this visit and not yet found finished. */
  private readonly watching = new Set<string>();
  /** Watched runs whose by-id lookup is in flight. */
  private readonly lookingUp = new Set<string>();
  private readonly queue: Notice[] = [];
  private shown: MatSnackBarRef<TextOnlySnackBar> | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.queue.length = 0;
      this.shown?.dismiss();
    });
  }

  /** Watches a run the page just started, before any reload lists it. */
  watch(id: string): void {
    this.watching.add(id);
  }

  /**
   * Takes one reload of the run lists and returns whether a watched run left the queue. With a truncated queue a
   * missing run may still be running, so only the finished list can end it.
   */
  update(
    activeIds: ReadonlySet<string>,
    queueTruncated: boolean,
    finished: readonly AnalysisRunDto[],
  ): boolean {
    let ended = false;
    for (const id of this.watching) {
      if (activeIds.has(id)) continue;
      const run = finished.find((r) => r.id === id);
      if (run) {
        ended = true;
        this.end(run);
      } else if (!queueTruncated && !this.lookingUp.has(id)) {
        // Not in the (filtered, limited) finished list: ask for the run itself.
        ended = true;
        this.lookUp(id);
      }
    }
    for (const id of activeIds) this.watching.add(id);
    return ended;
  }

  private lookUp(id: string): void {
    this.lookingUp.add(id);
    this.analysis
      .getRun(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (run) => {
          this.lookingUp.delete(id);
          if (run.status !== 'queued' && run.status !== 'running') this.end(run);
        },
        error: (error: unknown) => {
          this.lookingUp.delete(id);
          // A run that is gone has nothing to say; anything else is retried on the next reload.
          if (error instanceof HttpErrorResponse && error.status === 404) this.watching.delete(id);
        },
      });
  }

  private end(run: AnalysisRunDto): void {
    if (!this.watching.delete(run.id)) return;
    const notice = noticeOf(run);
    if (!notice) return;
    this.queue.push(notice);
    if (!this.shown) this.showNext();
  }

  private showNext(): void {
    const notice = this.queue.shift();
    if (!notice) {
      this.shown = null;
      return;
    }
    const ref = this.snackBar.open(notice.message, notice.action, { duration: FINISH_NOTICE_MS });
    this.shown = ref;
    ref
      .onAction()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (notice.link) {
          this.router.navigate([notice.link.path], { queryParams: notice.link.queryParams });
        }
      });
    ref
      .afterDismissed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.showNext());
  }
}
