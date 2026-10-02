import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { JobsService } from '../core/jobs.service';
import { humanise } from '../dashboard/fetch.models';
import { activeRunView, AnalysisRunDto, runTarget, savingsText } from './analysis.models';

/** Active runs with live progress and Cancel, then the finished runs with their counters and savings. */
@Component({
  selector: 'app-run-list',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  template: `
    <section class="flex flex-col gap-3" aria-labelledby="runs-active">
      <h2 id="runs-active" class="card-title">Run queue</h2>
      @for (v of activeViews(); track v.run.id) {
        <div class="flex flex-col gap-1" data-testid="active-run">
          <div class="flex flex-wrap items-center gap-2">
            <span class="min-w-0 flex-1 break-all">
              {{ target(v.run) }} · {{ v.run.requestedCount | number }} emails
            </span>
            <mat-chip-set>
              <mat-chip disableRipple data-testid="run-status">{{ label(v.status) }}</mat-chip>
            </mat-chip-set>
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
          </div>
          <mat-progress-bar
            [mode]="v.percent === null ? 'indeterminate' : 'determinate'"
            [value]="v.percent ?? 0"
            [attr.aria-label]="'Progress of ' + target(v.run)"
            data-testid="run-progress"
          />
          @if (v.progress?.message; as message) {
            <span class="muted text-sm" data-testid="run-message">{{ message }}</span>
          }
        </div>
      } @empty {
        <p class="muted m-0" data-testid="no-active-runs">No runs queued or running.</p>
      }
    </section>

    <section class="mt-6 flex flex-col gap-3" aria-labelledby="runs-finished">
      <h2 id="runs-finished" class="card-title">Recent runs</h2>
      @for (run of finished(); track run.id) {
        <div class="run flex flex-col gap-1 pt-3" data-testid="finished-run">
          <div class="flex flex-wrap items-center gap-2">
            <span class="min-w-0 flex-1 break-all">{{ target(run) }}</span>
            <span class="muted text-sm">{{ run.finishedAt ?? run.createdAt | date: 'short' }}</span>
            <mat-chip-set>
              <mat-chip
                disableRipple
                [class.failed]="run.status === 'failed'"
                data-testid="run-status"
              >
                {{ label(run.status) }}
              </mat-chip>
            </mat-chip-set>
          </div>
          <span data-testid="run-savings">{{ savings(run) }}</span>
          <span class="muted text-sm">
            {{ run.messagesLlm | number }} by the LLM · {{ run.messagesDerived | number }} derived ·
            {{ run.messagesFromMemory | number }} from memory · {{ run.groups | number }} groups
            @if (run.failedMessages > 0) {
              · {{ run.failedMessages | number }} failed
            }
            @if (run.skippedMessages > 0) {
              · {{ run.skippedMessages | number }} skipped
            }
          </span>
          @if (run.error) {
            <span class="error flex items-center gap-1 text-sm" role="note" data-testid="run-error">
              <mat-icon aria-hidden="true">error</mat-icon>{{ run.error }}
            </span>
          }
        </div>
      } @empty {
        <p class="muted m-0" data-testid="no-finished-runs">No finished runs yet.</p>
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
    mat-chip.failed {
      --mat-chip-label-text-color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunList {
  private readonly jobs = inject(JobsService);

  readonly active = input<readonly AnalysisRunDto[]>([]);
  readonly finished = input<readonly AnalysisRunDto[]>([]);
  /** Run ids whose cancel request is in flight or waiting for the run to stop. */
  readonly cancelling = input<ReadonlySet<string>>(new Set());
  readonly cancelRun = output<AnalysisRunDto>();

  readonly activeViews = computed(() =>
    this.active().map((run) =>
      activeRunView(run, run.jobId ? this.jobs.job(run.jobId) : undefined),
    ),
  );

  target(run: AnalysisRunDto): string {
    return runTarget(run);
  }

  label(status: string): string {
    return humanise(status);
  }

  savings(run: AnalysisRunDto): string {
    return savingsText(run.llmCalls, run.messagesCovered, run.savedPercent);
  }
}
