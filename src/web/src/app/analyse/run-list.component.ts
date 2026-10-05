import { DatePipe, DecimalPipe } from '@angular/common';
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
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { sentMessage } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { humanise } from '../dashboard/fetch.models';
import { SettingsService } from '../settings/settings.service';
import {
  activeRunView,
  AnalysisRunDto,
  canResume,
  modelCountersText,
  requestedText,
  runTarget,
  savingsText,
  TOP_SENDERS,
  usageText,
} from './analysis.models';
import { MAX_COMPARE, RUN_ACTIVE_TOOLTIP, RUN_TOO_LARGE_TOOLTIP } from './compare-run';

/**
 * Active runs with live progress and Cancel (a stalled one with Resume), then the finished runs, optionally only the
 * failed ones, with their counters, savings, model usage, error and Resume and, for top senders runs, the policies
 * proposed.
 */
@Component({
  selector: 'app-run-list',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    RouterLink,
  ],
  template: `
    <section class="flex flex-col gap-3" aria-labelledby="runs-active">
      <h2 id="runs-active" class="card-title">Run queue</h2>
      @for (v of activeViews(); track v.run.id) {
        <div class="flex flex-col gap-1" data-testid="active-run">
          <div class="flex flex-wrap items-center gap-2">
            <span class="min-w-0 flex-1 break-all">
              {{ target(v.run) }} · {{ requested(v.run) }}
            </span>
            <mat-chip-set>
              @if (v.run.isStalled) {
                <mat-chip
                  disableRipple
                  class="failed"
                  matTooltip="No job is running this run any more. Resume continues it."
                  data-testid="run-stalled"
                >
                  Stalled
                </mat-chip>
              } @else {
                <mat-chip disableRipple data-testid="run-status">{{ label(v.status) }}</mat-chip>
              }
            </mat-chip-set>
            @if (v.run.isStalled) {
              <button
                mat-button
                type="button"
                (click)="resumeRun.emit(v.run)"
                [disabled]="resuming().has(v.run.id)"
                [attr.aria-label]="'Resume run ' + target(v.run)"
                data-testid="resume-run"
              >
                {{ resuming().has(v.run.id) ? 'Resuming…' : 'Resume' }}
              </button>
            } @else {
              <button
                mat-button
                type="button"
                (click)="cancelRun.emit(v.run)"
                [disabled]="cancelling().has(v.run.id)"
                [attr.aria-label]="'Cancel run ' + target(v.run)"
                data-testid="cancel-run"
              >
                {{ cancelling().has(v.run.id) ? 'Cancelling…' : 'Cancel' }}
              </button>
            }
          </div>
          @if (!v.run.isStalled) {
            <mat-progress-bar
              [mode]="v.percent === null ? 'indeterminate' : 'determinate'"
              [value]="v.percent ?? 0"
              [attr.aria-label]="'Progress of ' + target(v.run)"
              data-testid="run-progress"
            />
            @if (v.progress?.message; as message) {
              <span class="muted text-sm" data-testid="run-message">{{ message }}</span>
            }
          }
        </div>
      } @empty {
        <p class="muted m-0" data-testid="no-active-runs">No runs queued or running.</p>
      }
    </section>

    <section class="mt-6 flex flex-col gap-3" aria-labelledby="runs-finished">
      <div class="flex flex-wrap items-center gap-2">
        <h2 id="runs-finished" class="card-title flex-1">Recent runs</h2>
        <mat-chip-listbox aria-label="Filter recent runs by status">
          <mat-chip-option
            [selected]="failedOnly()"
            (selectionChange)="failedOnlyChange.emit($event.selected)"
            data-testid="filter-failed"
          >
            Failed
          </mat-chip-option>
        </mat-chip-listbox>
      </div>
      @for (run of finished(); track run.id) {
        <div class="run flex flex-col gap-1 pt-3" data-testid="finished-run">
          <div class="flex flex-wrap items-center gap-2">
            <span class="min-w-0 flex-1 break-all">{{ target(run) }}</span>
            <span class="muted text-sm">{{ run.finishedAt ?? run.createdAt | date: 'short' }}</span>
            <mat-chip-set>
              <mat-chip
                disableRipple
                [class.failed]="run.status === 'failed'"
                [matTooltip]="run.status === 'failed' ? (run.error ?? '') : ''"
                data-testid="run-status"
              >
                {{ label(run.status) }}
              </mat-chip>
            </mat-chip-set>
          </div>
          <div class="flex flex-wrap items-center gap-2">
            <span class="min-w-0 flex-1" data-testid="run-savings">{{ savings(run) }}</span>
            @if (resumable(run)) {
              <button
                mat-button
                type="button"
                (click)="resumeRun.emit(run)"
                [disabled]="resuming().has(run.id)"
                [attr.aria-label]="'Resume run ' + target(run)"
                data-testid="resume-run"
              >
                {{ resuming().has(run.id) ? 'Resuming…' : 'Resume' }}
              </button>
            }
            @if (run.policiesProposed > 0) {
              <a
                mat-button
                routerLink="/policies"
                [queryParams]="{ status: 'proposed' }"
                data-testid="run-review-policies"
              >
                Review policies
              </a>
            }
            @if (run.messagesCovered > 0 && !isTopSenders(run)) {
              <button
                mat-button
                type="button"
                [disabled]="!!reanalyseBlocked(run)"
                [disabledInteractive]="true"
                [matTooltip]="reanalyseBlocked(run)"
                (click)="reanalyseRun.emit(run)"
                [attr.aria-label]="'Re-analyse run ' + target(run)"
                data-testid="run-reanalyse"
              >
                Re-analyse
              </button>
            }
            @if (claudeEnabled() && run.groups > 0 && !isTopSenders(run)) {
              <button
                mat-button
                type="button"
                [disabled]="sending().has(run.id)"
                (click)="sendToClaude(run)"
                [attr.aria-label]="'Send run to Claude: ' + target(run)"
                data-testid="run-claude"
              >
                Send run to Claude
              </button>
            }
          </div>
          <span class="muted text-sm">
            {{ run.messagesLlm | number }} by the LLM · {{ run.messagesDerived | number }} derived ·
            {{ run.messagesFromMemory | number }} from memory · {{ run.groups | number }} groups
            @if (run.failedMessages > 0) {
              · {{ run.failedMessages | number }} failed
            }
            @if (run.skippedMessages > 0) {
              · {{ run.skippedMessages | number }} skipped
            }
            @if (isTopSenders(run)) {
              <span data-testid="run-policies">
                · {{ run.policiesProposed | number }}
                {{ run.policiesProposed === 1 ? 'policy' : 'policies' }} proposed
              </span>
            }
          </span>
          @if (counters(run); as c) {
            <span class="muted text-sm" data-testid="run-counters">{{ c }}</span>
          }
          @if (usage(run); as u) {
            <span class="muted flex flex-wrap items-center gap-1 text-sm" data-testid="run-usage">
              {{ u }}
              @if (run.nearContextLimit > 0) {
                <mat-icon
                  class="warn icon-sm"
                  tabindex="0"
                  role="img"
                  [attr.aria-label]="contextWarning(run)"
                  [matTooltip]="contextWarning(run)"
                  data-testid="run-near-context"
                  >warning</mat-icon
                >
              }
            </span>
          }
          @if (run.error) {
            <div class="error flex items-start gap-1 text-sm" role="note">
              <mat-icon aria-hidden="true">error</mat-icon>
              <span
                class="min-w-0 flex-1 break-words"
                [class.truncate]="!expanded().has(run.id)"
                [id]="'run-error-' + run.id"
                data-testid="run-error"
                >{{ run.error }}</span
              >
              <button
                mat-button
                type="button"
                (click)="toggleError(run.id)"
                [attr.aria-expanded]="expanded().has(run.id)"
                [attr.aria-controls]="'run-error-' + run.id"
                data-testid="run-error-toggle"
              >
                {{ expanded().has(run.id) ? 'Less' : 'More' }}
              </button>
            </div>
          }
        </div>
      } @empty {
        <p class="muted m-0" data-testid="no-finished-runs">
          {{ failedOnly() ? 'No failed runs.' : 'No finished runs yet.' }}
        </p>
      }
    </section>
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
    .run + .run {
      border-top: 1px solid var(--mat-sys-outline-variant);
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .icon-sm {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    mat-chip.failed {
      --mat-chip-label-text-color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunList {
  private readonly jobs = inject(JobsService);
  private readonly claude = inject(ClaudeService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly settings = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(catchError(() => of(null))),
    { initialValue: null },
  );

  readonly active = input<readonly AnalysisRunDto[]>([]);
  readonly finished = input<readonly AnalysisRunDto[]>([]);
  /** Run ids whose cancel request is in flight or waiting for the run to stop. */
  readonly cancelling = input<ReadonlySet<string>>(new Set());
  readonly cancelRun = output<AnalysisRunDto>();
  /** Run ids whose resume request is in flight. */
  readonly resuming = input<ReadonlySet<string>>(new Set());
  /** "Resume" on a failed or stalled run. */
  readonly resumeRun = output<AnalysisRunDto>();
  /** The "Failed" filter chip: the page lists only failed finished runs. */
  readonly failedOnly = input(false);
  readonly failedOnlyChange = output<boolean>();
  /** Run ids whose error line is expanded. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** An analysis run is queued or running: no re-analysis can start. */
  readonly runActive = input(false);
  /** "Re-analyse" on a finished run: the page confirms and starts a re-analysis of it. */
  readonly reanalyseRun = output<AnalysisRunDto>();

  /** Why a run's "Re-analyse" is off (its tooltip), `''` when it may start. */
  reanalyseBlocked(run: AnalysisRunDto): string {
    if (this.runActive()) return RUN_ACTIVE_TOOLTIP;
    return run.messagesCovered > MAX_COMPARE ? RUN_TOO_LARGE_TOOLTIP : '';
  }

  readonly claudeEnabled = computed(() => (this.settings()?.claudeReviewerMode ?? 'off') !== 'off');
  /** Run ids whose "Send run to Claude" is in flight. */
  readonly sending = signal<ReadonlySet<string>>(new Set());

  readonly activeViews = computed(() =>
    this.active().map((run) =>
      activeRunView(run, run.jobId ? this.jobs.job(run.jobId) : undefined),
    ),
  );

  /** Queues the run's groups that still have pending members. */
  sendToClaude(run: AnalysisRunDto): void {
    if (this.sending().has(run.id)) return;
    this.sending.update((s) => new Set(s).add(run.id));
    this.claude
      .createReviews({ runId: run.id })
      .pipe(
        finalize(() =>
          this.sending.update((s) => {
            const next = new Set(s);
            next.delete(run.id);
            return next;
          }),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      // The error interceptor shows the server's problem detail.
      .subscribe({
        next: (r) => this.snackBar.open(sentMessage(r), 'Dismiss', { duration: 4000 }),
        error: () => undefined,
      });
  }

  toggleError(id: string): void {
    this.expanded.update((e) => {
      const next = new Set(e);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  resumable(run: AnalysisRunDto): boolean {
    return canResume(run);
  }

  counters(run: AnalysisRunDto): string | null {
    return modelCountersText(run);
  }

  target(run: AnalysisRunDto): string {
    return runTarget(run);
  }

  requested(run: AnalysisRunDto): string {
    return requestedText(run);
  }

  isTopSenders(run: AnalysisRunDto): boolean {
    return run.scope === TOP_SENDERS;
  }

  usage(run: AnalysisRunDto): string | null {
    return usageText(run);
  }

  contextWarning(run: AnalysisRunDto): string {
    const n = run.nearContextLimit;
    return `${n.toLocaleString()} ${n === 1 ? 'model call' : 'model calls'} filled at least 90 % of the model's context window: part of the prompt may have been cut off`;
  }

  label(status: string): string {
    return humanise(status);
  }

  savings(run: AnalysisRunDto): string {
    return savingsText(run.llmCalls, run.messagesCovered, run.savedPercent);
  }
}
