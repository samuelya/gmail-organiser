import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { filter, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { errorMessage } from '../core/error.interceptor';
import { relativeTime } from '../senders/senders.models';
import { FindingCard } from './finding-card.component';
import { RulesClaude } from './rules-claude.service';
import { FilterFindingDto, FilterReviewDto, fixView, groupFindings } from './rules.models';
import { RulesService } from './rules.service';

/**
 * The Rules page's Findings tab: runs the filter review and lists its findings by kind, each with
 * Apply fix / Dismiss, plus the local model's summary. The summary lives on the review, so it is
 * back after a tab switch.
 */
@Component({
  selector: 'app-findings-tab',
  imports: [
    FindingCard,
    MatButtonModule,
    MatExpansionModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
  ],
  providers: [RulesClaude],
  templateUrl: './findings-tab.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .summary {
      border: 1px solid var(--mat-sys-outline-variant);
      white-space: pre-line;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FindingsTab {
  private readonly rules = inject(RulesService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  readonly claude = inject(RulesClaude);

  /** Null with `loaded()` set: no review yet. */
  readonly review = signal<FilterReviewDto | null>(null);
  readonly loaded = signal(false);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  readonly reviewing = signal(false);
  readonly summarising = signal(false);
  /** Set when the API answered 409: the review has no findings. */
  readonly nothingToSummarise = signal(false);
  /** Finding ids with an apply or dismiss in flight. */
  readonly busy = signal<ReadonlySet<string>>(new Set());

  readonly grouped = computed(() => groupFindings(this.review()?.findings ?? []));
  readonly openCount = computed(() =>
    this.grouped().groups.reduce((n, g) => n + g.findings.length, 0),
  );
  readonly ago = (iso: string) => relativeTime(iso);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.rules
      .latestReview()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.loading.set(false);
          this.loaded.set(true);
          this.review.set(r);
        },
        error: () => {
          this.loading.set(false);
          this.loadFailed.set(true);
        },
      });
  }

  /** 503 (Gmail not connected or rate-limiting) shows in a snackbar via the error interceptor. */
  startReview(): void {
    if (this.reviewing()) return;
    this.reviewing.set(true);
    this.rules
      .startReview()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.reviewing.set(false);
          this.loaded.set(true);
          this.loadFailed.set(false);
          this.nothingToSummarise.set(false);
          this.review.set(r);
        },
        error: () => this.reviewing.set(false),
      });
  }

  apply(f: FilterFindingDto): void {
    const fix = fixView(f);
    if (!fix) return;
    const parts = [
      fix.create ? `creates "${fix.create.criteria}"` : '',
      fix.deletes.length
        ? `deletes ${fix.deletes.length} ${fix.deletes.length === 1 ? 'filter' : 'filters'}`
        : '',
    ].filter(Boolean);
    openConfirm(this.dialog, {
      title: 'Apply this fix?',
      message: `${fix.title}: ${parts.join(' and ')} in Gmail. Mail already labelled keeps its labels.`,
      confirm: 'Apply fix',
    })
      .pipe(
        filter(Boolean),
        switchMap(() => {
          this.setBusy(f.id, true);
          return this.rules.applyFinding(f.id);
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (updated) => {
          this.setBusy(f.id, false);
          this.replace(updated);
          this.refresh();
        },
        // The finding stays open with the reason, so Apply fix can be tried again.
        error: (e: unknown) => {
          this.setBusy(f.id, false);
          const error = e instanceof HttpErrorResponse ? errorMessage(e) : 'The fix failed.';
          this.replace({ ...f, error });
        },
      });
  }

  dismiss(f: FilterFindingDto): void {
    this.setBusy(f.id, true);
    this.rules
      .dismissFinding(f.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updated) => {
          this.setBusy(f.id, false);
          this.replace(updated);
        },
        error: () => this.setBusy(f.id, false),
      });
  }

  /** An LLM failure comes back as `summaryError`; only the summary fields of the answer are taken. */
  summarise(): void {
    const review = this.review();
    if (!review || this.summarising()) return;
    this.summarising.set(true);
    this.nothingToSummarise.set(false);
    this.rules
      .summarise(review.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          this.summarising.set(false);
          this.review.update((cur) =>
            cur?.id === r.id
              ? {
                  ...cur,
                  summary: r.summary,
                  summaryModel: r.summaryModel,
                  summarisedAt: r.summarisedAt,
                  summaryError: r.summaryError,
                }
              : cur,
          );
        },
        error: (e: unknown) => {
          this.summarising.set(false);
          if (e instanceof HttpErrorResponse && e.status === 409) this.nothingToSummarise.set(true);
        },
      });
  }

  /**
   * Reloads the review in place after a fix, so other findings show the filters it deleted.
   * A failed reload keeps what is on show.
   */
  private refresh(): void {
    this.rules
      .latestReview()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (r) => {
          if (r) this.review.set(r);
        },
        error: () => undefined,
      });
  }

  private replace(updated: FilterFindingDto): void {
    this.review.update((r) =>
      r ? { ...r, findings: r.findings.map((f) => (f.id === updated.id ? updated : f)) } : r,
    );
  }

  private setBusy(id: string, busy: boolean): void {
    this.busy.update((s) => {
      const next = new Set(s);
      if (busy) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}
