import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MAX_COMPARE, RUN_ACTIVE_TOOLTIP } from '../analyse/compare-run';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { AlternativeCompare } from './alternative-compare.component';
import { AlternativeTarget } from './alternative.models';
import { CARD_TOO_LARGE_TOOLTIP } from './card-reanalyse';
import { ClaudeVerdict } from './claude-verdict.component';
import { LabelChangeChip } from './label-change-chip.component';
import { commonMailType, MailTypeChip } from './mail-type-chip.component';
import { MemberRow } from './member-row.component';
import {
  claudeCardTarget,
  confidenceRange,
  FlagLabels,
  groupOrigin,
  isNewGroupLabel,
  ReviewGroupDto,
  SuggestionDto,
} from './review.models';

/** Members shown per "Show more". */
export const MEMBERS_STEP = 20;

/** One group of a sender: its suggested outcome, group decisions and, expanded, its members. */
@Component({
  selector: 'app-group-card',
  imports: [
    AlternativeCompare,
    ClaudeVerdict,
    LabelChangeChip,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MailTypeChip,
    MatTooltipModule,
    MemberRow,
  ],
  template: `
    @let g = group();
    <mat-card appearance="outlined" data-testid="group-card">
      <mat-card-content class="flex flex-col gap-2">
        <div class="flex flex-wrap items-start gap-2">
          <h3 class="title m-0 min-w-0 flex-1" data-testid="group-title">{{ g.display }}</h3>
          <span class="muted text-sm"
            >{{ g.size }} {{ g.size === 1 ? 'message' : 'messages' }}</span
          >
        </div>
        <div class="muted text-sm" data-testid="group-origin">{{ origin() }}</div>
        <div class="flex flex-wrap items-center gap-2 text-sm">
          <app-mail-type-chip [type]="mailType()" />
          <span class="chip" data-testid="group-label"
            >{{ g.topicLabel }}
            @if (newLabel()) {
              <span class="new" data-testid="group-new-label">new</span>
            }
          </span>
          @if (g.newLabelPending) {
            <span class="new-pending" data-testid="group-new-label-pending"
              >new label — approve individually</span
            >
          }
          @if (g.documentTypeLabel) {
            <span class="chip" data-testid="document-type"
              >{{ g.documentTypeLabel }}
              @if (g.documentTypeIsNew) {
                <span class="new" data-testid="document-type-new">new</span>
              }
            </span>
          }
          @if (g.needsAction) {
            <span class="chip" data-testid="group-action">{{ labels().action }}</span>
          }
          @if (g.toBeDeleted) {
            <span class="chip" data-testid="group-delete">{{ labels().delete }}</span>
          }
          <app-label-change-chip [change]="g" />
          <span class="muted" data-testid="group-confidence">Confidence {{ confidence() }}</span>
          @if (claudeMode() !== 'off' && g.suggestedForClaude) {
            <span
              class="claude-hint"
              matTooltip="Worth a second opinion from Claude"
              data-testid="group-claude-hint"
              >Claude?</span
            >
          }
          @if (g.mixed) {
            <span
              class="warn inline-flex items-center gap-1"
              matTooltip="Members disagree; approving the card takes only the members with its outcome"
              data-testid="group-mixed"
              ><mat-icon class="small-icon" aria-hidden="true">warning</mat-icon>Mixed</span
            >
          }
        </div>
        @if (g.reason) {
          <p class="m-0 text-sm" data-testid="group-reason">{{ g.reason }}</p>
        }
        @if (claude(); as c) {
          <app-claude-verdict
            [mode]="claudeMode()"
            [request]="c.request"
            [review]="c.review"
            [labels]="labels()"
            [busy]="busy()"
            [sendable]="pending()"
            (changed)="claudeChange.emit($event)"
          />
        }
        @if (g.alternative; as alt) {
          <app-alternative-compare
            [current]="g"
            [alternative]="alt"
            [labels]="labels()"
            [busy]="busy()"
            (useNew)="useNew.emit({ group: g })"
            (keepCurrent)="keepCurrent.emit({ group: g })"
            (showMembers)="expanded.set(true)"
          />
        }
        <div class="flex flex-wrap items-center gap-2">
          <!-- Applied mail is read-only: only re-analysis and its Use new / Keep current. -->
          @if (actionable()) {
            <button
              mat-flat-button
              type="button"
              [disabled]="busy()"
              (click)="approveAll.emit()"
              data-testid="group-approve"
            >
              Approve all
            </button>
            <button
              mat-stroked-button
              type="button"
              [disabled]="busy()"
              (click)="rejectAll.emit()"
              data-testid="group-reject"
            >
              Reject all
            </button>
            <button
              mat-stroked-button
              type="button"
              [disabled]="busy()"
              (click)="editGroup.emit()"
              data-testid="group-edit"
            >
              Edit
            </button>
          }
          <button
            mat-stroked-button
            type="button"
            [disabled]="reanalyseTooltip() !== null || busy()"
            [disabledInteractive]="true"
            [matTooltip]="reanalyseTooltip() ?? 'Re-analyse with the current prompt'"
            (click)="reanalyseGroup.emit(g)"
            data-testid="group-reanalyse"
          >
            @if (cardReanalysing()) {
              <mat-spinner
                diameter="18"
                aria-label="Re-analysing"
                data-testid="group-reanalysing"
              />
            } @else {
              <mat-icon aria-hidden="true">refresh</mat-icon>
            }
            Re-analyse
          </button>
          <button
            mat-button
            type="button"
            class="ml-auto"
            (click)="expanded.set(!expanded())"
            [attr.aria-expanded]="expanded()"
            data-testid="group-expand"
          >
            <mat-icon aria-hidden="true">{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
            {{ expanded() ? 'Hide members' : 'Members' }}
          </button>
        </div>
        @if (expanded()) {
          <div class="members" data-testid="group-members">
            @for (m of shownMembers(); track m.id) {
              <app-member-row
                [suggestion]="m"
                [labels]="labels()"
                [selected]="selected().has(m.id)"
                [skipped]="skipped().has(m.id)"
                [busy]="busy()"
                [runActive]="runActive()"
                [reanalysing]="!reanalysingCard() && reanalysing().has(m.id)"
                (reanalyse)="reanalyseMember.emit(m)"
                (approve)="approveMember.emit(m)"
                (reject)="rejectMember.emit(m)"
                (edit)="editMember.emit(m)"
                (toggleSelect)="toggleMember.emit(m)"
                [claudeMode]="g.groupKey === null ? 'off' : claudeMode()"
                (claudeChange)="claudeChange.emit($event)"
                (useNew)="useNew.emit({ member: m })"
                (keepCurrent)="keepCurrent.emit({ member: m })"
              />
            }
            @if (shownMembers().length < g.members.length) {
              <button
                mat-button
                type="button"
                (click)="shown.set(shown() + step)"
                data-testid="group-more"
              >
                Show more ({{ g.members.length - shownMembers().length }} left)
              </button>
            }
            @if (g.truncated) {
              <p class="muted m-0 text-sm" data-testid="group-truncated">
                Showing the newest {{ g.members.length }} of {{ g.size }} members.
              </p>
            }
          </div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .title {
      font: var(--mat-sys-title-small);
      overflow-wrap: anywhere;
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .chip {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      border-radius: 0.5rem;
      padding: 0.125rem 0.5rem;
      overflow-wrap: anywhere;
    }
    .new {
      margin-left: 0.25rem;
      font-weight: 600;
      color: var(--mat-sys-primary);
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .new-pending {
      border: 1px solid var(--mat-sys-tertiary);
      color: var(--mat-sys-tertiary);
      border-radius: 0.5rem;
      padding: 0 0.375rem;
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
    .members > * + * {
      border-top: 1px solid var(--mat-sys-outline-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GroupCard {
  readonly group = input.required<ReviewGroupDto>();
  readonly senderAddress = input.required<string>();
  readonly claudeMode = input<ClaudeReviewerMode>('off');
  readonly labels = input.required<FlagLabels>();
  readonly selected = input<ReadonlySet<string>>(new Set());
  readonly skipped = input<ReadonlySet<string>>(new Set());
  readonly busy = input(false);
  /** An analysis run is queued or running: "Re-analyse" waits. */
  readonly runActive = input(false);
  /** Suggestion ids of the re-analysis started on this page, until its run ends. */
  readonly reanalysing = input<ReadonlySet<string>>(new Set());
  /** That re-analysis was started from a whole card rather than one member. */
  readonly reanalysingCard = input(false);
  readonly approveAll = output<void>();
  readonly rejectAll = output<void>();
  /** The #120 edit dialog hook: the page opens it for this group. */
  readonly editGroup = output<void>();
  readonly approveMember = output<SuggestionDto>();
  readonly rejectMember = output<SuggestionDto>();
  readonly editMember = output<SuggestionDto>();
  readonly toggleMember = output<SuggestionDto>();
  /** "Re-analyse" of every loaded member, any status. */
  readonly reanalyseGroup = output<ReviewGroupDto>();
  readonly reanalyseMember = output<SuggestionDto>();
  /** A Claude item of the card or one of its members changed. A message analysed on its own shows its item on the card only. */
  readonly claudeChange = output<ExternalReviewDto>();
  /** "Use new" / "Keep current" on the card's or a member's re-analysis result. */
  readonly useNew = output<AlternativeTarget>();
  readonly keepCurrent = output<AlternativeTarget>();

  readonly step = MEMBERS_STEP;
  readonly expanded = signal(false);
  readonly shown = signal(MEMBERS_STEP);
  readonly shownMembers = computed(() => this.group().members.slice(0, this.shown()));
  readonly origin = computed(() => groupOrigin(this.group()));
  readonly confidence = computed(() => confidenceRange(this.group()));
  readonly newLabel = computed(() => isNewGroupLabel(this.group()));
  /** The listed members' mail type when they share one. */
  readonly mailType = computed(() => commonMailType(this.group().members));
  /** Applied members can no longer change. */
  readonly actionable = computed(() => this.group().members.some((m) => m.status !== 'applied'));
  readonly pending = computed(() => this.group().members.some((m) => m.status === 'pending'));
  /** Why "Re-analyse" is off (a tooltip), `null` when it may run. */
  readonly reanalyseTooltip = computed(() => {
    if (this.runActive()) return RUN_ACTIVE_TOOLTIP;
    if (this.group().members.length > MAX_COMPARE) return CARD_TOO_LARGE_TOOLTIP;
    return null;
  });
  readonly cardReanalysing = computed(() => {
    const ids = this.reanalysing();
    return this.reanalysingCard() && this.group().members.some((m) => ids.has(m.id));
  });
  readonly claude = computed(() => claudeCardTarget(this.senderAddress(), this.group()));
}
