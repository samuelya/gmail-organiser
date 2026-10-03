import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeReviewerMode } from '../settings/settings.models';
import { ClaudeVerdict } from './claude-verdict.component';
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
    ClaudeVerdict,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
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
          <span class="chip" data-testid="group-label"
            >{{ g.topicLabel }}
            @if (newLabel()) {
              <span class="new" data-testid="group-new-label">new</span>
            }
          </span>
          @if (g.needsAction) {
            <span class="chip" data-testid="group-action">{{ labels().action }}</span>
          }
          @if (g.toBeDeleted) {
            <span class="chip" data-testid="group-delete">{{ labels().delete }}</span>
          }
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
        <div class="flex flex-wrap items-center gap-2">
          <button
            mat-flat-button
            type="button"
            [disabled]="busy() || !actionable()"
            (click)="approveAll.emit()"
            data-testid="group-approve"
          >
            Approve all
          </button>
          <button
            mat-stroked-button
            type="button"
            [disabled]="busy() || !actionable()"
            (click)="rejectAll.emit()"
            data-testid="group-reject"
          >
            Reject all
          </button>
          <button
            mat-stroked-button
            type="button"
            [disabled]="busy() || !actionable()"
            (click)="editGroup.emit()"
            data-testid="group-edit"
          >
            Edit
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
                (approve)="approveMember.emit(m)"
                (reject)="rejectMember.emit(m)"
                (edit)="editMember.emit(m)"
                (toggleSelect)="toggleMember.emit(m)"
                [claudeMode]="g.groupKey === null ? 'off' : claudeMode()"
                (claudeChange)="claudeChange.emit($event)"
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
  readonly approveAll = output<void>();
  readonly rejectAll = output<void>();
  /** The #120 edit dialog hook: the page opens it for this group. */
  readonly editGroup = output<void>();
  readonly approveMember = output<SuggestionDto>();
  readonly rejectMember = output<SuggestionDto>();
  readonly editMember = output<SuggestionDto>();
  readonly toggleMember = output<SuggestionDto>();
  /** A Claude item of the card or one of its members changed. A message analysed on its own shows its item on the card only. */
  readonly claudeChange = output<ExternalReviewDto>();

  readonly step = MEMBERS_STEP;
  readonly expanded = signal(false);
  readonly shown = signal(MEMBERS_STEP);
  readonly shownMembers = computed(() => this.group().members.slice(0, this.shown()));
  readonly origin = computed(() => groupOrigin(this.group()));
  readonly confidence = computed(() => confidenceRange(this.group()));
  readonly newLabel = computed(() => isNewGroupLabel(this.group()));
  /** Applied members can no longer change. */
  readonly actionable = computed(() => this.group().members.some((m) => m.status !== 'applied'));
  readonly pending = computed(() => this.group().members.some((m) => m.status === 'pending'));
  readonly claude = computed(() => claudeCardTarget(this.senderAddress(), this.group()));
}
