import { inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map, Subject } from 'rxjs';
import { parseReviewParams, ReviewStatus } from './review.models';

/**
 * The Review page's tab and selected sender, fed from the query string next to `mailType`. A tab change replaces
 * the history entry and drops the sender; a sender selection is a normal navigation, so Back returns to the last.
 */
@Injectable()
export class ReviewUrl {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly statusState = signal<ReviewStatus>('pending');
  private readonly selectedState = signal<string | null>(null);
  readonly status = this.statusState.asReadonly();
  readonly selected = this.selectedState.asReadonly();
  /** The URL changed the tab or the selected sender. */
  readonly changed = new Subject<'status' | 'sender'>();

  /** The sender `?sender=` names, if any. */
  private named: string | null = null;
  private firstListed: string | null = null;
  private listed = false;

  constructor() {
    this.route.queryParamMap
      .pipe(map(parseReviewParams), takeUntilDestroyed())
      .subscribe(({ status, sender }) => this.onParams(status, sender));
  }

  setStatus(status: ReviewStatus): void {
    if (status !== this.status()) this.navigate({ status, sender: null }, true);
  }

  setSender(address: string): void {
    if (address !== this.selected()) this.navigate({ sender: address }, false);
  }

  /**
   * A senders list arrived. A tab or filter change moves to its first sender unless the selected one is in it;
   * a sender a link names stays selected on load, even when it is not on the first page.
   */
  onList(addresses: readonly string[], changed: boolean): void {
    const first = !this.listed;
    this.listed = true;
    this.firstListed = addresses[0] ?? null;
    const selected = this.selected();
    if (first && selected && selected === this.named) return;
    if (selected && !(changed && !addresses.includes(selected))) return;
    this.selectedState.set(this.firstListed);
    if (this.named) this.navigate({ sender: null }, true);
  }

  /** A tab change keeps the selected sender until the new list arrives; Back to no sender selects the first listed. */
  private onParams(status: ReviewStatus, sender: string | null): void {
    const hadSender = this.named !== null;
    this.named = sender;
    if (status !== this.status()) {
      this.statusState.set(status);
      this.changed.next('status');
    } else if (!sender && hadSender) sender = this.firstListed;
    if (sender && sender !== this.selected()) {
      this.selectedState.set(sender);
      this.changed.next('sender');
    }
  }

  private navigate(queryParams: Record<string, string | null>, replaceUrl: boolean): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
