import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { isReconnecting, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { jobStatusLabel, jobTypeLabel } from '../dashboard/fetch.models';

/**
 * Toolbar button with the running-jobs count and a menu of active jobs. The layout loads it with
 * `@defer`, so the SignalR client, menu and badge stay out of the initial bundle.
 */
@Component({
  selector: 'app-jobs-indicator',
  imports: [
    MatBadgeModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatTooltipModule,
    RouterLink,
  ],
  templateUrl: './jobs-indicator.html',
  styles: `
    .jobs-status {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JobsIndicator {
  private readonly jobs = inject(JobsService);
  protected readonly jobTypeLabel = jobTypeLabel;
  protected readonly jobStatusLabel = jobStatusLabel;
  protected readonly progressPercent = progressPercent;

  protected readonly activeJobs = this.jobs.activeJobs;
  protected readonly reconnecting = computed(() => isReconnecting(this.jobs.connectionState()));
  protected readonly label = computed(() => {
    const n = this.activeJobs().length;
    const running = `${n} job${n === 1 ? '' : 's'} running`;
    return this.reconnecting() ? `${running}, live updates reconnecting` : running;
  });
}
