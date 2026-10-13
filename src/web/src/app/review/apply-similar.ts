import { DestroyRef, inject, Injectable } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { filter, map, Observable, Subject, switchMap } from 'rxjs';
import {
  AlternativeDecision,
  AlternativeDecisionResponse,
  alternativeMessage,
  AlternativeTarget,
} from './alternative.models';
import {
  ApplySimilarDialog,
  ApplySimilarDialogData,
  ApplySimilarResult,
} from './apply-similar-dialog.component';
import { CardReanalyse } from './card-reanalyse';
import { SimilarPendingDto } from './review.models';
import { ReviewService } from './review.service';

/** The suggestion an accepted re-analysis came from and the comment that produced it. */
interface SimilarSource {
  id: string;
  comment: string;
}

/** A member accept names the member; a card accept its first loaded member. */
export function similarSource(target: AlternativeTarget): SimilarSource | null {
  if ('member' in target) {
    return { id: target.member.id, comment: target.member.alternative?.comment ?? '' };
  }
  const first = target.group.members[0];
  return first ? { id: first.id, comment: target.group.alternative?.comment ?? '' } : null;
}

/**
 * "Apply to similar" after a "Use new" on the Review page: looks up the sender's similar pending emails and, when
 * there are any, offers to re-analyse them with the same comment (a re-analysis followed by `CardReanalyse`) or to
 * copy the accepted outcome to them. The results wait for "Use new" or "Keep current". Provided by the page.
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

  /**
   * The snackbar after a "Use new" or "Keep current". `target` is the one decided on, as listed before the
   * decision (the refresh drops its alternative and comment).
   */
  decided(
    decision: AlternativeDecision,
    target: AlternativeTarget,
    r: AlternativeDecisionResponse,
  ): void {
    const message = alternativeMessage(decision, r);
    this.snackBar.open(message, 'Dismiss', { duration: 6000 });
    const source = decision === 'accept' && r.accepted > 0 ? similarSource(target) : null;
    if (!source) return;
    this.review
      .similar(source.id)
      .pipe(
        filter((s) => s.count > 0 && s.suggestionIds.length > 0),
        switchMap((s) =>
          this.snackBar
            .open(`Accepted. ${similarCount(s.count)}.`, 'Apply to similar…', { duration: 10_000 })
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

  /** The dialog's choice; nothing when cancelled or once a run became active. */
  private open(similar: SimilarPendingDto, comment: string): Observable<ApplySimilarResult> {
    const data: ApplySimilarDialogData = { similar, comment, blocked: this.reanalyse.blocked };
    return this.dialog
      .open<ApplySimilarDialog, ApplySimilarDialogData, ApplySimilarResult>(ApplySimilarDialog, {
        data,
      })
      .afterClosed()
      .pipe(
        filter((choice): choice is ApplySimilarResult => typeof choice === 'object' && !!choice),
        filter(() => !this.reanalyse.blocked()),
      );
  }

  private start(sourceId: string, ids: string[], choice: ApplySimilarResult): void {
    if (choice.mode === 'reanalyse') {
      this.reanalyse.similar(ids, choice.comment);
      return;
    }
    this.review
      .copyAlternatives({ sourceSuggestionId: sourceId, suggestionIds: ids })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.snackBar.open(
            `Copied to ${r.copied.toLocaleString()} ${r.copied === 1 ? 'email' : 'emails'}; compare them under Re-analysed`,
            'Dismiss',
            { duration: 6000 },
          );
          this.copiedSubject.next();
        },
        error: () => undefined,
      });
  }
}

function similarCount(n: number): string {
  return `${n.toLocaleString()} similar pending ${n === 1 ? 'email' : 'emails'}`;
}
