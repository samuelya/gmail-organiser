import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeVerdict } from '../review/claude-verdict.component';
import { relativeTime } from '../senders/senders.models';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { actionChips, FilterFindingDto, fixView } from './rules.models';

/**
 * One filter-review finding: the filters involved, the proposed fix, and while it is open Claude's verdict
 * ("Send to Claude") and Apply fix / Dismiss.
 */
@Component({
  selector: 'app-finding-card',
  imports: [ClaudeVerdict, MatButtonModule, MatChipsModule, MatIconModule],
  template: `
    @let f = finding();
    <article class="finding flex flex-col gap-3 rounded-lg p-4" data-testid="finding">
      <div class="flex flex-wrap items-start gap-2">
        <p class="m-0 flex-1" data-testid="finding-description">{{ f.description }}</p>
        @if (carried()) {
          <span class="badge text-sm" data-testid="finding-earlier">From an earlier review</span>
        }
      </div>

      <ul class="m-0 flex list-none flex-col gap-2 p-0" aria-label="Filters involved">
        @for (filter of f.filters; track filter.id) {
          <li class="flex flex-wrap items-center gap-2" data-testid="finding-filter">
            <span class="break-all" [class.line-through]="!!filter.deletedAt">{{
              filter.criteriaSummary
            }}</span>
            @if (filter.deletedAt) {
              <span class="muted text-sm">(deleted)</span>
            }
            <mat-chip-set [attr.aria-label]="'Actions of ' + filter.criteriaSummary">
              @for (chip of chips(filter.action); track chip) {
                <mat-chip>{{ chip }}</mat-chip>
              }
            </mat-chip-set>
          </li>
        }
      </ul>

      @if (fix(); as x) {
        <div class="fix flex flex-col gap-2 rounded-md p-3" data-testid="finding-fix">
          <span class="font-medium">{{ x.title }}</span>
          @if (x.create; as c) {
            <div class="flex flex-wrap items-center gap-2" data-testid="fix-create">
              <span class="muted text-sm">Creates</span>
              <span class="break-all">{{ c.criteria }}</span>
              <mat-chip-set aria-label="Actions of the new filter">
                @for (chip of c.chips; track chip) {
                  <mat-chip>{{ chip }}</mat-chip>
                }
              </mat-chip-set>
            </div>
          }
          @for (d of x.deletes; track d) {
            <div class="flex flex-wrap items-center gap-2" data-testid="fix-delete">
              <span class="muted text-sm">Deletes</span>
              <span class="break-all">{{ d }}</span>
            </div>
          }
        </div>
      } @else {
        <p class="muted m-0 text-sm" data-testid="finding-report-only">
          Report only: there is no safe automatic fix.
        </p>
      }

      @if (f.error && f.status === 'open') {
        <p class="m-0 flex items-center gap-2" role="alert" data-testid="finding-error">
          <mat-icon aria-hidden="true">error</mat-icon>{{ f.error }}
        </p>
      }

      @if (f.status === 'open' && claudeMode(); as mode) {
        <app-claude-verdict
          [mode]="mode"
          [request]="{ findingIds: [f.id] }"
          [review]="claudeReview()"
          [busy]="busy()"
          [showWhenOff]="true"
          (changed)="claudeChanged.emit($event)"
        />
      }

      @if (f.status === 'open') {
        <div class="flex flex-wrap justify-end gap-2">
          <button
            mat-button
            type="button"
            [disabled]="busy()"
            (click)="dismiss.emit(f)"
            data-testid="finding-dismiss"
          >
            Dismiss
          </button>
          @if (fix()) {
            <button
              mat-flat-button
              type="button"
              [disabled]="busy()"
              (click)="apply.emit(f)"
              data-testid="finding-apply"
            >
              {{ carried() ? 'Resume fix' : 'Apply fix' }}
            </button>
          }
        </div>
      } @else {
        <p class="muted m-0 text-sm" data-testid="finding-status">{{ statusText() }}</p>
      }
    </article>
  `,
  styles: `
    .finding {
      border: 1px solid var(--mat-sys-outline-variant);
    }
    .fix {
      background: var(--mat-sys-surface-container);
    }
    .badge {
      color: var(--mat-sys-on-tertiary-container);
      background: var(--mat-sys-tertiary-container);
      border-radius: 0.5rem;
      padding: 0.125rem 0.5rem;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FindingCard {
  readonly finding = input.required<FilterFindingDto>();
  /** The id of the review on show; a finding from another review was carried over half-applied. */
  readonly reviewId = input.required<string>();
  readonly busy = input(false);
  /** Null: no Claude panel (resolved findings). */
  readonly claudeMode = input<ClaudeReviewerMode | null>(null);
  readonly claudeReview = input<ExternalReviewDto | null>(null);
  readonly claudeChanged = output<ExternalReviewDto>();
  readonly apply = output<FilterFindingDto>();
  readonly dismiss = output<FilterFindingDto>();

  readonly chips = actionChips;
  readonly fix = computed(() => fixView(this.finding()));
  readonly carried = computed(() => this.finding().reviewId !== this.reviewId());
  readonly statusText = computed(() => {
    const f = this.finding();
    switch (f.status) {
      case 'applied':
        return f.appliedAt ? `Applied ${relativeTime(f.appliedAt)}` : 'Applied';
      case 'dismissed':
        return 'Dismissed';
      default:
        return 'Superseded by a newer review';
    }
  });
}
