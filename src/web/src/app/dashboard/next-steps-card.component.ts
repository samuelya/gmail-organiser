import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AnalysisSummaryStore } from '../analyse/analysis-summary-store';
import { FetchStatusDto } from './fetch.models';
import { nextSteps } from './next-steps';

/**
 * Dashboard card: up to three things to do next, each with a link to the page that does it. The summary
 * comes from the page's `AnalysisSummaryStore`; status and Gmail connection are inputs.
 */
@Component({
  selector: 'app-next-steps-card',
  imports: [MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule, RouterLink],
  template: `
    @if (loading() || steps().length > 0) {
      <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-next-steps">
        <mat-card-content class="flex flex-col gap-3" data-testid="next-steps-card">
          <h2 id="dashboard-next-steps" class="card-title">Next steps</h2>
          @if (loading()) {
            <mat-progress-bar mode="indeterminate" aria-label="Loading the next steps" />
          } @else {
            <ul class="m-0 flex list-none flex-col gap-2 p-0">
              @for (step of steps(); track step.id) {
                <li
                  class="flex flex-wrap items-center gap-2"
                  [attr.data-testid]="'step-' + step.id"
                >
                  <mat-icon aria-hidden="true">{{ step.icon }}</mat-icon>
                  <span class="min-w-0 flex-1">{{ step.text }}</span>
                  @if (step.link; as link) {
                    <a
                      mat-stroked-button
                      [routerLink]="link"
                      [queryParams]="step.queryParams"
                      [attr.data-testid]="'step-link-' + step.id"
                    >
                      {{ step.linkLabel }}
                    </a>
                  }
                </li>
              }
            </ul>
          }
        </mat-card-content>
      </mat-card>
    }
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextStepsCard {
  private readonly store = inject(AnalysisSummaryStore);

  /** The page's fetch status; `null` until loaded. */
  readonly status = input<FetchStatusDto | null>(null);
  /** The page's last fetch status request failed. */
  readonly statusFailed = input(false);
  /** Gmail is connected; `null` while unknown. */
  readonly gmailConnected = input<boolean | null>(null);
  /** The page's Gmail connection check failed. */
  readonly gmailFailed = input(false);

  /** An input still loading; a failed one counts as done and its steps are left out. */
  readonly loading = computed(
    () =>
      (this.status() === null && !this.statusFailed()) ||
      (this.gmailConnected() === null && !this.gmailFailed()) ||
      (this.store.summary() === null && !this.store.loadFailed()),
  );
  /** A failed reload keeps the last value on the page; the steps leave it out rather than show stale counts. */
  readonly steps = computed(() =>
    nextSteps({
      gmailConnected: this.gmailFailed() ? null : this.gmailConnected(),
      status: this.statusFailed() ? null : this.status(),
      summary: this.store.loadFailed() ? null : this.store.summary(),
    }),
  );
}
