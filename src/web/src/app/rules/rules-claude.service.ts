import { computed, DestroyRef, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of } from 'rxjs';
import { ExternalReviewDto, ruleTargetKey } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { SettingsService } from '../settings/settings.service';

/**
 * The Claude reviewer mode and the latest Claude review item per label plan and filter finding, for one Rules tab
 * (provide it on the component). Loads the newest items, then follows `externalReviewChanged`; reloads after a
 * reconnect, since changes are missed while disconnected.
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

  constructor() {
    this.jobs.externalReviewChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((item) => this.patch(item));
    effect(() => {
      this.jobs.reconnects();
      untracked(() => this.load());
    });
  }

  forPlan(id: string): ExternalReviewDto | null {
    return this.items().get(`plan:${id}`) ?? null;
  }

  forFinding(id: string): ExternalReviewDto | null {
    return this.items().get(`finding:${id}`) ?? null;
  }

  /** Keeps the newest item per target; a change to the item on show replaces it. */
  patch(item: ExternalReviewDto): void {
    const key = ruleTargetKey(item);
    if (!key) return;
    const current = this.items().get(key);
    if (current && current.id !== item.id && current.createdAt > item.createdAt) return;
    const next = new Map(this.items());
    next.set(key, item);
    this.items.set(next);
  }

  private load(): void {
    this.claude
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        // Oldest first, so the newest item per target wins.
        next: (page) => [...page.items].reverse().forEach((item) => this.patch(item)),
        error: () => undefined,
      });
  }
}
