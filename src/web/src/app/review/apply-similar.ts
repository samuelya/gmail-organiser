import { DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { filter, map, Observable, Subject, switchMap, tap } from 'rxjs';
import { RUN_ACTIVE_TOOLTIP } from '../analyse/compare-run';
import { plural } from '../clean-up/clean-up.models';
import {
  AlternativeDecision,
  AlternativeDecisionResponse,
  alternativeMessage,
  alternativeRequest,
  AlternativeTarget,
  repends,
  skippedNote,
} from './alternative.models';
import {
  ApplySimilarDialog,
  ApplySimilarDialogData,
  ApplySimilarResult,
} from './apply-similar-dialog.component';
import { CardReanalyse } from './card-reanalyse';
import { ReviewStatus, SimilarPendingDto } from './review.models';
import { ReviewService } from './review.service';

/** The suggestion an accepted re-analysis came from and the comment that produced it. */
interface SimilarSource {
  id: string;
  comment: string;
}

/**
 * A member accept names the member; a card accept its first loaded member with a re-analysis result. A mixed card
 * accepted several outcomes, so it has no single source.
 */
export function similarSource(target: AlternativeTarget): SimilarSource | null {
  if ('member' in target) {
    return { id: target.member.id, comment: target.member.alternative?.comment ?? '' };
  }
  const g = target.group;
  if (g.alternative?.mixed) return null;
  const source = g.members.find((m) => !!m.alternative);
  if (!source) return null;
  return { id: source.id, comment: source.alternative?.comment ?? g.alternative?.comment ?? '' };
}

/**
 * "Use new" and "Keep current" on the Review page, and the "Apply to similar" that follows a "Use new": looks up the
 * sender's similar pending emails and, when there are any, offers to re-analyse them with the same comment (a
 * re-analysis followed by `CardReanalyse`) or to copy the accepted outcome to them. The results wait for "Use new" or
 * "Keep current". Provided by the page.
 */
@Injectable()
export class ApplySimilar {
  private readonly review = inject(ReviewService);
  private readonly reanalyse = inject(CardReanalyse);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly copiedSubject = new Subject<void>();
  /** Alternatives were copied: what is shown changed. */
  readonly copied = this.copiedSubject.asObservable();
  /** "Use new" re-pended an approved or applied suggestion. */
  readonly pendingAgain = signal(false);

  /**
   * "Use new" (accept) or "Keep current" (discard) for a member or a whole card of the sender's `status` tab. The
   * caller subscribes (and refreshes after); the snackbar and the offer follow the answer.
   */
  decide(
    decision: AlternativeDecision,
    target: AlternativeTarget,
    senderAddress: string,
    status: ReviewStatus,
  ): Observable<AlternativeDecisionResponse> {
    const request = alternativeRequest(target, senderAddress, status);
    const call =
      decision === 'accept'
        ? this.review.acceptAlternatives(request)
        : this.review.discardAlternatives(request);
    return call.pipe(
      tap((r) => {
        if (decision === 'accept' && r.accepted > 0 && repends(target)) this.pendingAgain.set(true);
        this.decided(decision, target, r);
      }),
    );
  }

  /** `target` is the one decided on, as listed before the decision (the refresh drops its alternative and comment). */
  private decided(
    decision: AlternativeDecision,
    target: AlternativeTarget,
    r: AlternativeDecisionResponse,
  ): void {
    this.snackBar.open(alternativeMessage(decision, r), 'Dismiss', { duration: 6000 });
    const source = decision === 'accept' && r.accepted > 0 ? similarSource(target) : null;
    if (!source) return;
    // The offer replaces the decision snackbar, so it repeats what was skipped.
    const accepted = r.skipped ? `Accepted. ${skippedNote(r.skipped)}` : 'Accepted.';
    this.review
      .similar(source.id)
      .pipe(
        filter((s) => s.count > 0 && s.suggestionIds.length > 0),
        switchMap((s) =>
          this.snackBar
            .open(`${accepted} ${plural(s.count, 'similar pending email')}.`, 'Apply to similar…', {
              duration: 10_000,
            })
            .onAction()
            .pipe(
              switchMap(() => this.open(s, source.comment)),
              map((choice) => ({ ids: s.suggestionIds, choice })),
            ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      // The error interceptor shows the server's problem detail.
      .subscribe({
        next: ({ ids, choice }) => this.start(source.id, ids, choice),
        error: () => undefined,
      });
  }

  /** The dialog's choice; nothing when cancelled. */
  private open(similar: SimilarPendingDto, comment: string): Observable<ApplySimilarResult> {
    const data: ApplySimilarDialogData = { similar, comment, blocked: this.reanalyse.blocked };
    return this.dialog
      .open<ApplySimilarDialog, ApplySimilarDialogData, ApplySimilarResult>(ApplySimilarDialog, {
        data,
      })
      .afterClosed()
      .pipe(
        filter((choice): choice is ApplySimilarResult => typeof choice === 'object' && !!choice),
      );
  }

  /** A run that became active after the confirm stops a re-analysis here; a copy gets the API's `409`. */
  private start(sourceId: string, ids: string[], choice: ApplySimilarResult): void {
    if (choice.mode === 'reanalyse') {
      if (this.reanalyse.blocked()) {
        this.snackBar.open(`${RUN_ACTIVE_TOOLTIP}; try again once it ends.`, 'Dismiss', {
          duration: 6000,
        });
        return;
      }
      this.reanalyse.similar(ids, choice.comment);
      return;
    }
    this.review
      .copyAlternatives({ sourceSuggestionId: sourceId, suggestionIds: ids })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          const skipped = r.skipped ? `; ${plural(r.skipped, 'email')} skipped` : '';
          this.snackBar.open(
            `Copied to ${plural(r.copied, 'email')}${skipped}; compare them under Re-analysed`,
            'Dismiss',
            { duration: 6000 },
          );
          this.copiedSubject.next();
        },
        error: () => undefined,
      });
  }
}
