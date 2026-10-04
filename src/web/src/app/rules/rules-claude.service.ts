import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, expand, map, merge, Observable, of, Subject, switchMap } from 'rxjs';
import { ExternalReviewDto, ruleTargetKey } from '../core/claude.models';
import { ClaudeService, ReviewListTargets } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { SettingsService } from '../settings/settings.service';

/** The label plan and filter findings a Rules tab shows; their review items are the ones loaded. */
export interface RuleTargets {
  labelPlanId?: string | null;
  findingIds?: readonly string[];
}

/** The API's largest page and the most `findingId` values one list request takes. */
const CHUNK = 100;

/**
 * The Claude reviewer mode and the latest Claude review item per label plan and filter finding, for one Rules tab
 * (provide it on the component, which calls `follow`). Loads the items of the tab's own plan or findings, however
 * many newer items exist for other targets, then follows `externalReviewChanged`; reloads when the targets change
 * and after a reconnect, since changes are missed while disconnected.
 */
@Injectable()
export class RulesClaude {
  private readonly claude = inject(ClaudeService);
  private readonly jobs = inject(JobsService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly settings = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(
        map((s) => s.claudeReviewerMode),
        catchError(() => of(null)),
      ),
    { initialValue: null },
  );
  readonly mode = computed<ClaudeReviewerMode>(() => this.settings() ?? 'off');
  private readonly items = signal<ReadonlyMap<string, ExternalReviewDto>>(new Map());
  private readonly source = signal<() => RuleTargets>(() => ({}));
  private readonly targets = computed(() => this.source()(), { equal: sameTargets });
  /** Each load cancels the one in flight, so items of targets no longer shown stop arriving. */
  private readonly loads = new Subject<RuleTargets>();

  constructor() {
    this.jobs.externalReviewChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((item) => this.patch(item));
    this.loads
      .pipe(
        switchMap((t) => this.fetch(t)),
        takeUntilDestroyed(this.destroyRef),
      )
      // Oldest first, so the newest item per target wins.
      .subscribe((items) => [...items].reverse().forEach((item) => this.patch(item)));
    effect(() => {
      this.jobs.reconnects();
      const targets = this.targets();
      untracked(() => this.loads.next(targets));
    });
  }

  /** Loads the review items of the plan and findings `targets` returns, again whenever their ids change. */
  follow(targets: () => RuleTargets): void {
    this.source.set(targets);
  }

  forPlan(id: string): ExternalReviewDto | null {
    return this.items().get(`plan:${id}`) ?? null;
  }

  forFinding(id: string): ExternalReviewDto | null {
    return this.items().get(`finding:${id}`) ?? null;
  }

  /**
   * Keeps the newest item per target; a change to the item on show replaces it unless it is an older copy (a list
   * response or a POST reply that arrives after a live change).
   */
  patch(item: ExternalReviewDto): void {
    const key = ruleTargetKey(item);
    if (!key) return;
    const current = this.items().get(key);
    if (current && current.id !== item.id && current.createdAt > item.createdAt) return;
    if (current && current.id === item.id && isStale(current, item)) return;
    const next = new Map(this.items());
    next.set(key, item);
    this.items.set(next);
  }

  /** One request for the plan and one per chunk of finding ids, each walking its pages; a failed one loads nothing. */
  private fetch(targets: RuleTargets): Observable<readonly ExternalReviewDto[]> {
    const ids = targets.findingIds ?? [];
    const lists: ReviewListTargets[] = [];
    if (targets.labelPlanId) lists.push({ labelPlanId: targets.labelPlanId });
    for (let i = 0; i < ids.length; i += CHUNK) lists.push({ findingIds: ids.slice(i, i + CHUNK) });
    return merge(...lists.map((t) => this.pages(t)));
  }

  private pages(targets: ReviewListTargets): Observable<readonly ExternalReviewDto[]> {
    const page = (n: number) => this.claude.list(n, CHUNK, targets).pipe(catchError(() => EMPTY));
    return page(1).pipe(
      expand((p) =>
        p.items.length > 0 && p.page * p.pageSize < p.total ? page(p.page + 1) : EMPTY,
      ),
      map((p) => p.items),
    );
  }
}

function sameTargets(a: RuleTargets, b: RuleTargets): boolean {
  const x = a.findingIds ?? [];
  const y = b.findingIds ?? [];
  return (
    (a.labelPlanId ?? null) === (b.labelPlanId ?? null) &&
    x.length === y.length &&
    x.every((id, i) => id === y[i])
  );
}

/**
 * True when `next` is an older copy of `current`. Reviewed and resolved never move back; within one attempt queued
 * comes before running. A retry moves unavailable or cancelled back to queued, so those may be replaced.
 */
function isStale(current: ExternalReviewDto, next: ExternalReviewDto): boolean {
  if (current.resolvedAt) return !next.resolvedAt;
  if (current.status === 'reviewed') return next.status !== 'reviewed';
  return current.status === 'running' && next.status === 'queued';
}
