import { inject, Injectable, Signal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { catchError, EMPTY, map, Observable, of, Subject, switchMap } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { MailTypeFilter } from './mail-type-filter';
import {
  parseReviewParams,
  ReviewSenderDto,
  ReviewStatus,
  SENDER_PAGE_SIZE,
} from './review.models';
import { ReviewService } from './review.service';

/** The page's senders list and "Re-analysed" filter, which decide whether a sender is listed. */
export interface ReviewListState {
  senders: Signal<PagedDto<ReviewSenderDto> | null>;
  reanalysed: Signal<boolean>;
}

/**
 * The Review page's tab and selected sender, fed from the query string next to `mailType`. A tab change replaces
 * the history entry and drops the sender; a sender selection is a normal navigation, so Back returns to the last.
 * Every entry names the sender it shows, so Back lands on that sender, not on whatever the list shows now.
 */
@Injectable()
export class ReviewUrl {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly review = inject(ReviewService);
  private readonly mailTypes = inject(MailTypeFilter);

  private readonly statusState = signal<ReviewStatus>('pending');
  private readonly selectedState = signal<string | null>(null);
  private readonly listedState = signal(true);
  readonly status = this.statusState.asReadonly();
  readonly selected = this.selectedState.asReadonly();
  /** False while a sender the URL named is not known to be listed: its detail waits, so it is never a 404. */
  readonly listed = this.listedState.asReadonly();
  /** The URL changed the tab or the selected sender. */
  readonly changed = new Subject<'status' | 'sender'>();

  private list: ReviewListState | null = null;
  /** The selected sender came from the URL (a link, Back or Forward) and no list has shown it yet. */
  private fromUrl = false;
  private readonly lookups = new Subject<string | null>();
  /** Params of navigations not yet seen in the URL, so a second one in the same tick does not drop the first's. */
  private pending: Record<string, string | null> = {};

  constructor() {
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe((params) => this.onParams(params));
    this.lookups
      .pipe(
        switchMap((address) => (address ? this.find(address) : EMPTY)),
        takeUntilDestroyed(),
      )
      .subscribe((found) => (found ? this.keep(found) : this.selectFirst()));
  }

  connect(list: ReviewListState): void {
    this.list = list;
  }

  setStatus(status: ReviewStatus): void {
    if (status !== this.status()) this.navigate({ status, sender: null }, true);
  }

  setSender(address: string): void {
    if (sameAddress(address, this.selected())) return;
    this.select(address);
    this.navigate({ sender: address }, false);
  }

  /**
   * A senders list arrived. A sender the URL named stays selected when the API lists it under the current tab and
   * filter, even off this page; otherwise a tab or filter change moves to the first sender unless the selected one
   * is in it.
   */
  onList(changed: boolean): void {
    const items = this.list?.senders()?.items;
    const selected = this.selected();
    if (!items) return;
    if (!selected) return this.selectFirst();
    const listed = items.find((s) => sameAddress(s.address, selected));
    if (listed) return this.keep(listed.address);
    if (this.fromUrl) return this.lookups.next(selected);
    if (changed) this.selectFirst();
  }

  private onParams(params: ParamMap): void {
    this.pending = {};
    const { status, sender } = parseReviewParams(params);
    this.normalise(params, status, sender);
    const tab = status !== this.status();
    if (tab) {
      this.lookups.next(null);
      this.statusState.set(status);
      this.changed.next('status');
    }
    // A tab change keeps the selected sender until the new list arrives; an entry without one shows the first.
    if (!sender) {
      if (!tab) this.selectFirst();
      return;
    }
    if (!tab && sameAddress(sender, this.selected())) return;
    this.select(sender);
    this.fromUrl = true;
    this.listedState.set(false);
    // The list shown is this tab's; after a tab change the new list calls `onList`.
    if (!tab) this.onList(false);
  }

  /** `?status=bogus` or a blank sender is replaced, so a tab click and a shared link see what the page shows. */
  private normalise(params: ParamMap, status: ReviewStatus, sender: string | null): void {
    const fixed: Record<string, string | null> = {};
    const rawStatus = params.get('status');
    const rawSender = params.get('sender');
    if (rawStatus !== null && rawStatus !== status) fixed['status'] = status;
    if (rawSender !== null && rawSender !== sender) fixed['sender'] = sender;
    if (Object.keys(fixed).length) this.navigate(fixed, true);
  }

  /** The address as the API lists it under the current tab and filter, or null when it has no suggestions there. */
  private find(address: string): Observable<string | null> {
    return this.review
      .listSenders(
        this.status(),
        address,
        1,
        SENDER_PAGE_SIZE,
        this.list?.reanalysed() ?? false,
        this.mailTypes.selected(),
      )
      .pipe(
        map((page) => page.items.find((s) => sameAddress(s.address, address))?.address ?? null),
        catchError(() => of(null)),
      );
  }

  private select(address: string | null): void {
    this.lookups.next(null);
    this.fromUrl = false;
    this.listedState.set(true);
    if (address === this.selected()) return;
    this.selectedState.set(address);
    this.changed.next('sender');
  }

  /** Keeps a listed sender, spelt as the list spells it, and writes it to the current entry. */
  private keep(address: string): void {
    this.select(address);
    this.replaceSender(address);
  }

  private selectFirst(): void {
    const first = this.list?.senders()?.items[0]?.address ?? null;
    this.select(first);
    this.replaceSender(first);
  }

  private replaceSender(address: string | null): void {
    if (this.route.snapshot.queryParamMap.get('sender') !== address)
      this.navigate({ sender: address }, true);
  }

  private navigate(queryParams: Record<string, string | null>, replaceUrl: boolean): void {
    this.pending = { ...this.pending, ...queryParams };
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: this.pending,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}

/** Addresses compare case-insensitively: a link may spell one differently from the list. */
function sameAddress(a: string, b: string | null): boolean {
  return a.toLowerCase() === b?.toLowerCase();
}
