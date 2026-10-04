import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { finalize, Observable } from 'rxjs';
import { AnalysisService } from '../analyse/analysis.service';
import {
  confirmCompareRun,
  injectAnalysisRunActive,
  MAX_COMPARE,
  RUN_ACTIVE_TOOLTIP,
} from '../analyse/compare-run';
import { MAX_ANALYSE_INDIVIDUALLY } from './review.models';
import { ReviewService } from './review.service';

/** Tooltip of "Analyse individually" while the selection holds an applied email. */
export const APPLIED_SELECTED_TOOLTIP = 'Applied emails can only be re-analysed';

/** "Analyse individually (n)" and "Re-analyse (n)" for the ticked members of the Review sender header. */
@Component({
  selector: 'app-review-selection-actions',
  imports: [MatButtonModule, MatTooltipModule],
  template: `
    @let n = selection().size;
    <button
      mat-stroked-button
      type="button"
      [disabled]="analyseBlocked() !== null || n === 0"
      [disabledInteractive]="true"
      [matTooltip]="analyseBlocked() || ''"
      (click)="analyseIndividually()"
      data-testid="analyse-individually"
    >
      Analyse individually ({{ n }})
    </button>
    <button
      mat-stroked-button
      type="button"
      [disabled]="reanalyseBlocked() !== null || n === 0"
      [disabledInteractive]="true"
      [matTooltip]="reanalyseBlocked() || ''"
      (click)="reanalyse()"
      data-testid="reanalyse-selection"
    >
      Re-analyse ({{ n }})
    </button>
  `,
  styles: `
    :host {
      display: contents;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SelectionActions {
  private readonly review = inject(ReviewService);
  private readonly analysis = inject(AnalysisService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** Ticked suggestion ids. */
  readonly selection = input.required<ReadonlySet<string>>();
  /** The selection holds an applied suggestion. */
  readonly hasApplied = input(false);
  readonly busy = input(false);
  /** A run was queued for the selection: the page clears it and re-fetches. */
  readonly started = output<void>();

  private readonly runActive = injectAnalysisRunActive();
  private readonly starting = signal(false);

  /** Why "Analyse individually" is off (a tooltip), `''` when only busy, `null` when it may run. */
  readonly analyseBlocked = computed(() => {
    if (this.hasApplied()) return APPLIED_SELECTED_TOOLTIP;
    if (this.selection().size > MAX_ANALYSE_INDIVIDUALLY)
      return `At most ${MAX_ANALYSE_INDIVIDUALLY} at a time`;
    return this.busy() || this.starting() ? '' : null;
  });

  readonly reanalyseBlocked = computed(() => {
    if (this.runActive()) return RUN_ACTIVE_TOOLTIP;
    if (this.selection().size > MAX_COMPARE) return `At most ${MAX_COMPARE} at a time`;
    return this.busy() || this.starting() ? '' : null;
  });

  analyseIndividually(): void {
    const ids = [...this.selection()];
    if (ids.length === 0 || this.analyseBlocked() !== null) return;
    this.follow(this.review.analyseIndividually(ids), `${count(ids.length)} individually`);
  }

  /** Re-analyses the ticked members, any status; the results wait next to the current ones. */
  reanalyse(): void {
    const ids = [...this.selection()];
    if (ids.length === 0 || this.reanalyseBlocked() !== null) return;
    this.follow(
      confirmCompareRun(this.dialog, this.analysis, { suggestionIds: ids }, ids.length),
      `${count(ids.length)} again; compare the results here once it is done`,
      'Re-analysing',
    );
  }

  private follow(request: Observable<unknown>, what: string, verb = 'Analysing'): void {
    this.starting.set(true);
    request
      .pipe(
        finalize(() => this.starting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      // The error interceptor shows the server's problem detail (a 409 among them).
      .subscribe({
        next: () => {
          this.started.emit();
          this.snackBar
            .open(`${verb} ${what}.`, 'Analyse', { duration: 8000 })
            .onAction()
            .subscribe(() => void this.router.navigate(['/analyse']));
        },
        error: () => undefined,
      });
  }
}

function count(n: number): string {
  return `${n} ${n === 1 ? 'message' : 'messages'}`;
}
