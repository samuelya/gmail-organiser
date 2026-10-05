import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RUN_ACTIVE_TOOLTIP } from '../analyse/compare-run';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { AlternativeCompare } from './alternative-compare.component';
import { memberValues } from './alternative.models';
import { ClaudeVerdict } from './claude-verdict.component';
import { LabelChangeChip } from './label-change-chip.component';
import { MailTypeChip } from './mail-type-chip.component';
import { FlagLabels, percent, SOURCE_LABELS, SuggestionDto } from './review.models';

/** One suggestion inside a group card: preview, source, confidence and per-member decisions. */
@Component({
  selector: 'app-member-row',
  imports: [
    AlternativeCompare,
    ClaudeVerdict,
    DatePipe,
    LabelChangeChip,
    MailTypeChip,
    MatButtonModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  template: `
    @let s = suggestion();
    <div
      class="flex flex-wrap items-start gap-2 py-2"
      data-testid="member-row"
      [attr.data-id]="s.id"
    >
      <mat-checkbox
        [checked]="selected()"
        (change)="toggleSelect.emit()"
        [aria-label]="'Select ' + subject()"
        data-testid="member-select"
      />
      <div class="min-w-0 flex-1 basis-60">
        <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span class="subject min-w-0 font-medium">{{ subject() }}</span>
          <span class="muted text-sm">{{ s.date | date: 'mediumDate' }}</span>
          @if (s.currentLabels.length) {
            <span class="muted text-sm" data-testid="member-current-labels"
              >· Labels: {{ s.currentLabels.join(', ') }}</span
            >
          }
        </div>
        @if (s.snippet) {
          <p class="snippet muted m-0 text-sm">{{ s.snippet }}</p>
        }
        <div class="mt-1 flex flex-wrap items-center gap-2 text-sm">
          <span class="badge" data-testid="member-source">{{ source() }}</span>
          <span class="muted" [attr.aria-label]="'Confidence ' + confidence()">{{
            confidence()
          }}</span>
          @if (claudeMode() !== 'off' && s.suggestedForClaude) {
            <span
              class="claude-hint"
              matTooltip="Worth a second opinion from Claude"
              data-testid="member-claude-hint"
              >Claude?</span
            >
          }
          <app-mail-type-chip [type]="s.mailType" />
          @if (s.topicLabel) {
            <span class="badge">{{ s.topicLabel }}</span>
          }
          @if (s.newLabelPending) {
            <span class="new-pending" data-testid="member-new-label-pending"
              >new label — approve individually</span
            >
          }
          @if (s.documentTypeLabel) {
            <span class="badge" data-testid="document-type"
              >{{ s.documentTypeLabel }}
              @if (s.documentTypeIsNew) {
                <span class="new" data-testid="document-type-new">new</span>
              }
            </span>
          }
          @if (s.needsAction) {
            <span class="badge">{{ labels().action }}</span>
          }
          @if (s.toBeDeleted) {
            <span class="badge">{{ labels().delete }}</span>
          }
          <app-label-change-chip [change]="s" />
          @if (s.protected) {
            <mat-icon
              class="small-icon"
              matTooltip="Protected: never marked for deletion"
              aria-label="Protected: never marked for deletion"
              data-testid="member-protected"
              >shield</mat-icon
            >
          }
          <span class="muted" data-testid="member-status">{{ s.status }}</span>
          @if (skipped()) {
            <span class="skipped" data-testid="member-skipped">skipped</span>
          }
        </div>
        <app-claude-verdict
          class="mt-1 block"
          [mode]="claudeMode()"
          [request]="claudeRequest()"
          [review]="s.claudeReview"
          [labels]="labels()"
          [busy]="busy()"
          [sendable]="s.status === 'pending'"
          (changed)="claudeChange.emit($event)"
        />
        @if (s.alternative; as alt) {
          <app-alternative-compare
            class="mt-2 block"
            [current]="values()"
            [alternative]="alt"
            [labels]="labels()"
            [busy]="busy()"
            (useNew)="useNew.emit()"
            (keepCurrent)="keepCurrent.emit()"
          />
        }
      </div>
      <div class="ml-auto flex shrink-0">
        <!-- Applied mail is read-only: only re-analysis and its Use new / Keep current. -->
        @if (s.status !== 'applied') {
          @if (s.status !== 'approved') {
            <button
              mat-icon-button
              type="button"
              [disabled]="busy()"
              (click)="approve.emit()"
              matTooltip="Approve"
              [attr.aria-label]="'Approve ' + subject()"
              data-testid="member-approve"
            >
              <mat-icon aria-hidden="true">check</mat-icon>
            </button>
          }
          @if (s.status !== 'rejected') {
            <button
              mat-icon-button
              type="button"
              [disabled]="busy()"
              (click)="reject.emit()"
              matTooltip="Reject"
              [attr.aria-label]="'Reject ' + subject()"
              data-testid="member-reject"
            >
              <mat-icon aria-hidden="true">close</mat-icon>
            </button>
          }
          <button
            mat-icon-button
            type="button"
            [disabled]="busy()"
            (click)="edit.emit()"
            matTooltip="Edit"
            [attr.aria-label]="'Edit ' + subject()"
            data-testid="member-edit"
          >
            <mat-icon aria-hidden="true">edit</mat-icon>
          </button>
        }
        <button
          mat-icon-button
          type="button"
          [disabled]="busy() || runActive()"
          [disabledInteractive]="true"
          (click)="reanalyse.emit()"
          [matTooltip]="reanalyseTooltip()"
          [attr.aria-label]="'Re-analyse ' + subject()"
          data-testid="member-reanalyse"
        >
          @if (reanalysing()) {
            <mat-spinner diameter="20" aria-label="Re-analysing" data-testid="member-reanalysing" />
          } @else {
            <mat-icon aria-hidden="true">refresh</mat-icon>
          }
        </button>
      </div>
    </div>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .subject {
      overflow-wrap: anywhere;
    }
    .snippet {
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
      overflow-wrap: anywhere;
    }
    .badge {
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.5rem;
      padding: 0 0.375rem;
      overflow-wrap: anywhere;
    }
    .skipped {
      color: var(--mat-sys-error);
    }
    .new-pending {
      border: 1px solid var(--mat-sys-tertiary);
      color: var(--mat-sys-tertiary);
      border-radius: 0.5rem;
      padding: 0 0.375rem;
    }
    .new {
      margin-left: 0.25rem;
      font-weight: 600;
      color: var(--mat-sys-primary);
    }
    .claude-hint {
      color: var(--mat-sys-tertiary);
      font-weight: 600;
    }
    .small-icon {
      font-size: 1.125rem;
      width: 1.125rem;
      height: 1.125rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MemberRow {
  readonly suggestion = input.required<SuggestionDto>();
  readonly labels = input.required<FlagLabels>();
  readonly selected = input(false);
  /** The last group or bulk approve left this member pending. */
  readonly skipped = input(false);
  readonly busy = input(false);
  readonly claudeMode = input<ClaudeReviewerMode>('off');
  /** An analysis run is queued or running: "Re-analyse" waits. */
  readonly runActive = input(false);
  /** This member's own re-analysis is running. */
  readonly reanalysing = input(false);
  readonly approve = output<void>();
  readonly reject = output<void>();
  readonly edit = output<void>();
  readonly toggleSelect = output<void>();
  /** "Re-analyse" with the current prompt, any status. */
  readonly reanalyse = output<void>();
  readonly claudeChange = output<ExternalReviewDto>();
  /** "Use new" / "Keep current" on its re-analysis result. */
  readonly useNew = output<void>();
  readonly keepCurrent = output<void>();

  readonly subject = computed(() => this.suggestion().subject || '(no subject)');
  readonly source = computed(
    () => SOURCE_LABELS[this.suggestion().source] ?? this.suggestion().source,
  );
  readonly confidence = computed(() => percent(this.suggestion().confidence));
  readonly values = computed(() => memberValues(this.suggestion()));
  readonly reanalyseTooltip = computed(() =>
    this.runActive() ? RUN_ACTIVE_TOOLTIP : 'Re-analyse with the current prompt',
  );
  readonly claudeRequest = computed(() => ({ suggestionIds: [this.suggestion().id] }));
}
