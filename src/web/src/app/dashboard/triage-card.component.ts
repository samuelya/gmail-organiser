import { DatePipe, DecimalPipe } from '@angular/common';
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
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, Observable, of, Subject, switchMap } from 'rxjs';
import { TriageChart } from './triage-chart.component';
import { hoursText, percentText, TriageMetricsDto, triagePoints } from './triage.models';
import { TriageService } from './triage.service';

/** Dashboard card: policy and filter coverage, inbox unread, LLM hours, and their 90-day history. */
@Component({
  selector: 'app-triage-card',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
    TriageChart,
  ],
  template: `
    <mat-card appearance="outlined" role="region" aria-labelledby="dashboard-triage">
      <mat-card-content class="flex flex-col gap-4" data-testid="triage-card">
        <div class="flex flex-wrap items-center gap-2">
          <h2 id="dashboard-triage" class="card-title flex-1">Triage</h2>
          @if (metrics()?.current; as c) {
            <span class="muted text-sm" data-testid="triage-taken-at">
              As of {{ c.takenAt | date: 'medium' }}
            </span>
          }
          <button
            mat-stroked-button
            type="button"
            (click)="refresh()"
            [disabled]="busy()"
            data-testid="triage-refresh"
          >
            <mat-icon aria-hidden="true">refresh</mat-icon>
            Refresh
          </button>
        </div>

        @if (loadFailed()) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="triage-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            The triage metrics could not be {{ metrics() ? 'refreshed' : 'loaded' }}.
            <button mat-button type="button" (click)="load()">Retry</button>
          </p>
        } @else if (!metrics() || busy()) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading the triage metrics" />
        }

        @if (metrics(); as m) {
          @if (m.current; as c) {
            <dl class="m-0 grid grid-cols-2 gap-4 lg:grid-cols-4">
              <div>
                <dt>Covered by policy</dt>
                <dd data-testid="triage-policy">{{ figures()?.policy }}</dd>
              </div>
              <div>
                <dt>Covered by filter</dt>
                <dd data-testid="triage-filter">{{ figures()?.filter }}</dd>
              </div>
              <div>
                <dt>Inbox unread</dt>
                <dd data-testid="triage-unread">
                  {{ c.inboxUnreadCount | number }}
                  <span class="muted text-sm">of {{ c.inboxCount | number }}</span>
                </dd>
              </div>
              <div>
                <dt>LLM time</dt>
                <dd data-testid="triage-llm">{{ figures()?.llm }}</dd>
              </div>
            </dl>
          } @else {
            <p class="muted m-0" data-testid="triage-no-snapshot">
              No snapshot yet. Refresh takes the first one.
            </p>
          }
          @if (points().length > 0) {
            <app-triage-chart [points]="points()" />
          } @else {
            <p class="muted m-0" data-testid="triage-no-history">
              The chart fills in once snapshots exist; one is taken every hour and after each job.
            </p>
          }
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .card-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    dt {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    dd {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TriageCard {
  private readonly triage = inject(TriageService);
  /** Every request goes through here; `switchMap` drops an older response. */
  private readonly requests = new Subject<Observable<TriageMetricsDto>>();

  readonly metrics = signal<TriageMetricsDto | null>(null);
  readonly loadFailed = signal(false);
  /** A refresh snapshot is in flight. */
  readonly busy = signal(false);

  readonly figures = computed(() => {
    const m = this.metrics();
    const c = m?.current;
    if (!m || !c) return null;
    return {
      policy: percentText(c.policyCoverageRatio),
      filter: percentText(c.filterCoverageRatio),
      llm: hoursText(m.llmHours),
    };
  });
  readonly points = computed(() => triagePoints(this.metrics()?.history ?? []));

  constructor() {
    this.requests
      .pipe(
        switchMap((request) => request.pipe(catchError(() => of(null)))),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((metrics) => {
        if (metrics) this.metrics.set(metrics);
        this.loadFailed.set(!metrics);
        this.busy.set(false);
      });
    this.load();
  }

  load(): void {
    this.requests.next(this.triage.get());
  }

  /** Takes a snapshot now; the response carries the reloaded metrics. */
  refresh(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.requests.next(this.triage.snapshot());
  }
}
