import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { JobDto, progressPercent } from '../core/jobs.models';

/** A sender's active fetch job in its row: a small progress indicator, resume when paused, and cancel. */
@Component({
  selector: 'app-sender-progress',
  imports: [
    DecimalPipe,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  template: `
    <span class="inline-flex items-center gap-1" data-testid="sender-progress">
      @switch (job().status) {
        @case ('queued') {
          <span
            class="muted text-sm"
            matTooltip="Runs after the current fetch"
            data-testid="sender-queued"
            >Queued</span
          >
        }
        @case ('paused') {
          <span class="muted text-sm" data-testid="sender-paused">Paused</span>
          <button
            mat-icon-button
            type="button"
            (click)="resumeFetch.emit()"
            [disabled]="busy()"
            [matTooltip]="'Resume ' + fetchLabel()"
            [attr.aria-label]="'Resume ' + fetchLabel()"
            data-testid="sender-resume"
          >
            <mat-icon aria-hidden="true">play_arrow</mat-icon>
          </button>
        }
        @default {
          <mat-progress-spinner
            diameter="20"
            strokeWidth="3"
            [mode]="percent() === null ? 'indeterminate' : 'determinate'"
            [value]="percent() ?? 0"
            [attr.aria-label]="'Progress of ' + fetchLabel()"
          />
          <span class="muted text-sm" data-testid="sender-done">{{
            job().progress?.done ?? 0 | number
          }}</span>
        }
      }
      <button
        mat-icon-button
        type="button"
        (click)="cancelFetch.emit()"
        [disabled]="busy()"
        [matTooltip]="'Cancel ' + fetchLabel()"
        [attr.aria-label]="'Cancel ' + fetchLabel()"
        data-testid="sender-cancel"
      >
        <mat-icon aria-hidden="true">close</mat-icon>
      </button>
    </span>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SenderProgress {
  readonly job = input.required<JobDto>();
  /** The row's sender address, for labels. */
  readonly label = input.required<string>();
  /** The job is shown on other rows too, so it is a domain fetch; otherwise it may be either. */
  readonly domainFetch = input(false);
  /** A resume or cancel is waiting for the job's status to change. */
  readonly busy = input(false);
  readonly resumeFetch = output<void>();
  readonly cancelFetch = output<void>();

  /** The job has no target, so a lone row only says what it may cover. */
  readonly fetchLabel = computed(() =>
    this.domainFetch()
      ? `the domain fetch covering ${this.label()}`
      : `the fetch for ${this.label()} or its domain`,
  );

  readonly percent = computed(() => progressPercent(this.job().progress));
}
