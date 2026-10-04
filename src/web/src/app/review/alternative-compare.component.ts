import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  alternativeDiff,
  alternativeSource,
  CompareValues,
  SuggestionAlternativeDto,
} from './alternative.models';
import { LabelChangeChip } from './label-change-chip.component';
import { confidenceRange, FlagLabels, percent } from './review.models';

/**
 * A re-analysis result next to the current outcome: topic label, document type, label change, flags and
 * confidence, the new values that differ highlighted; "Use new" and "Keep current" decide. A group whose
 * members' results disagree shows the count and points to its members instead of one comparison.
 */
@Component({
  selector: 'app-alternative-compare',
  imports: [LabelChangeChip, MatButtonModule],
  template: `
    @let a = alternative();
    @let c = current();
    @let d = diff();
    <div
      class="box flex flex-col gap-2 p-2 text-sm"
      role="group"
      [attr.aria-label]="title()"
      data-testid="alternative"
    >
      <div class="flex flex-wrap items-center gap-2">
        <span class="font-medium">{{ title() }}</span>
        @if (source(); as s) {
          <span class="muted" data-testid="alternative-source">{{ s }}</span>
        }
      </div>
      @if (a.mixed) {
        <div class="flex flex-wrap items-center gap-2" data-testid="alternative-mixed">
          <span
            >{{ a.count }} {{ a.count === 1 ? 'member has' : 'members have' }} a new result, and
            they differ.</span
          >
          <button
            mat-button
            type="button"
            (click)="showMembers.emit()"
            data-testid="alternative-members"
          >
            Compare per member
          </button>
        </div>
      } @else {
        <div class="grid items-center gap-x-3 gap-y-1 sm:grid-cols-[5rem_minmax(0,1fr)]">
          <span class="muted">Current</span>
          <div class="flex flex-wrap items-center gap-2" data-testid="alternative-current">
            <span class="badge">{{ c.topicLabel }}</span>
            @if (c.documentTypeLabel) {
              <span class="badge">{{ c.documentTypeLabel }}</span>
            }
            @if (c.needsAction) {
              <span class="badge">{{ labels().action }}</span>
            }
            @if (c.toBeDeleted) {
              <span class="badge">{{ labels().delete }}</span>
            }
            <app-label-change-chip [change]="c" />
            <span class="muted">Confidence {{ currentConfidence() }}</span>
          </div>
          <span class="muted">New</span>
          <div class="flex flex-wrap items-center gap-2" data-testid="alternative-new">
            <span class="badge" [class.diff]="d.topicLabel" data-testid="alt-topic"
              >{{ a.topicLabel }}
              @if (d.topicLabel) {
                <span class="sr-only"> (changed)</span>
              }
            </span>
            @if (a.documentTypeLabel) {
              <span class="badge" [class.diff]="d.documentType" data-testid="alt-document-type"
                >{{ a.documentTypeLabel }}
                @if (d.documentType) {
                  <span class="sr-only"> (changed)</span>
                }
              </span>
            } @else if (d.documentType) {
              <span class="badge diff" data-testid="alt-document-type">No document type</span>
            }
            @if (a.needsAction) {
              <span class="badge" [class.diff]="d.needsAction" data-testid="alt-action"
                >{{ labels().action }}
                @if (d.needsAction) {
                  <span class="sr-only"> (changed)</span>
                }
              </span>
            } @else if (d.needsAction) {
              <span class="badge diff" data-testid="alt-action">No {{ labels().action }}</span>
            }
            @if (a.toBeDeleted) {
              <span class="badge" [class.diff]="d.toBeDeleted" data-testid="alt-delete"
                >{{ labels().delete }}
                @if (d.toBeDeleted) {
                  <span class="sr-only"> (changed)</span>
                }
              </span>
            } @else if (d.toBeDeleted) {
              <span class="badge diff" data-testid="alt-delete">No {{ labels().delete }}</span>
            }
            <span class="contents" [class.diff-chip]="d.labelChange" data-testid="alt-label-change">
              <app-label-change-chip [change]="a" />
              @if (d.labelChange) {
                <span class="sr-only">(label change differs)</span>
              }
            </span>
            <span [class.diff-text]="d.confidence" data-testid="alt-confidence"
              >Confidence {{ newConfidence() }}
              @if (d.confidence) {
                <span class="sr-only"> (changed)</span>
              }
            </span>
          </div>
        </div>
        @if (a.reason) {
          <p class="muted m-0" data-testid="alternative-reason">{{ a.reason }}</p>
        }
      }
      <div class="flex flex-wrap gap-2">
        <button
          mat-flat-button
          type="button"
          [disabled]="busy()"
          (click)="useNew.emit()"
          data-testid="alternative-use"
        >
          {{ a.count > 1 ? 'Use new (' + a.count + ')' : 'Use new' }}
        </button>
        <button
          mat-stroked-button
          type="button"
          [disabled]="busy()"
          (click)="keepCurrent.emit()"
          data-testid="alternative-keep"
        >
          Keep current
        </button>
      </div>
    </div>
  `,
  styles: `
    .box {
      border: 1px dashed var(--mat-sys-outline);
      border-radius: 0.5rem;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .badge {
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.5rem;
      padding: 0 0.375rem;
      overflow-wrap: anywhere;
    }
    .diff {
      border-color: var(--mat-sys-primary);
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
      font-weight: 600;
    }
    .diff-chip {
      --mat-chip-outline-color: var(--mat-sys-primary);
      --mat-chip-label-text-weight: 600;
    }
    .diff-text {
      color: var(--mat-sys-primary);
      font-weight: 600;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AlternativeCompare {
  readonly current = input.required<CompareValues>();
  readonly alternative = input.required<SuggestionAlternativeDto>();
  readonly labels = input.required<FlagLabels>();
  readonly busy = input(false);
  readonly useNew = output<void>();
  readonly keepCurrent = output<void>();
  /** A mixed group's results differ per member: the card shows its members. */
  readonly showMembers = output<void>();

  readonly diff = computed(() => alternativeDiff(this.current(), this.alternative()));
  readonly source = computed(() => alternativeSource(this.alternative()));
  readonly title = computed(() =>
    this.alternative().count > 1
      ? `Re-analysed: ${this.alternative().count} new results`
      : 'Re-analysed',
  );
  readonly currentConfidence = computed(() => confidenceRange(this.current()));
  readonly newConfidence = computed(() => percent(this.alternative().confidence));
}
