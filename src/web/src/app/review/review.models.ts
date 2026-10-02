import { inject, InjectionToken } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, of } from 'rxjs';

/** The statuses `GET /api/review/senders` and `…/senders/{address}` filter by. */
export type ReviewStatus = 'pending' | 'approved' | 'rejected';
export const REVIEW_STATUSES: readonly { value: ReviewStatus; label: string }[] = [
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
];

/** `SuggestionStatus` as the API writes it. */
export type SuggestionStatus = ReviewStatus | 'applied';
/** `SuggestionSource` as the API writes it. */
export type SuggestionSource = 'llm' | 'derived' | 'memory' | 'sender_pattern';

export const SENDER_PAGE_SIZE = 25;
/** The API's `ReviewQuery.MaxGroupPageSize` is 50. */
export const GROUP_PAGE_SIZE = 20;
/** The API's `AnalysisCandidates.MaxMessageIds`. */
export const MAX_ANALYSE_INDIVIDUALLY = 500;

export interface ReviewSenderDto {
  address: string;
  displayName: string | null;
  pending: number;
  approved: number;
  rejected: number;
  applied: number;
  totalMessages: number;
}

export interface SuggestionDto {
  id: string;
  messageId: string;
  subject: string | null;
  date: string;
  snippet: string | null;
  source: SuggestionSource;
  topicLabel: string;
  isNewLabel: boolean;
  needsAction: boolean;
  toBeDeleted: boolean;
  unsubscribeSuggested: boolean;
  confidence: number;
  reason: string;
  status: SuggestionStatus;
  edited: boolean;
  protected: boolean;
}

/** `groupKey` is null for a message analysed on its own. */
export interface ReviewGroupDto {
  groupKey: string | null;
  display: string;
  size: number;
  llmCount: number;
  derivedCount: number;
  memoryCount: number;
  topicLabel: string;
  needsAction: boolean;
  toBeDeleted: boolean;
  mixed: boolean;
  confidenceMin: number;
  confidenceMax: number;
  reason: string;
  members: SuggestionDto[];
  /** Not every member is listed; the counts cover them all. */
  truncated: boolean;
}

export interface ReviewSenderDetailDto {
  sender: ReviewSenderDto;
  groups: ReviewGroupDto[];
  page: number;
  pageSize: number;
  totalGroups: number;
}

/** The card's outcome; group approve takes only the members with exactly this outcome. */
export interface ReviewOutcome {
  topicLabel: string;
  needsAction: boolean;
  toBeDeleted: boolean;
}

export interface GroupDecisionResponse {
  changed: number;
  /** Pending members left pending: another outcome than the card's, or a protected deletion. */
  skipped: string[];
}

export interface BulkApproveRequest {
  threshold: number;
  includeDerived: boolean;
  senderAddress?: string;
}

export interface BulkApproveResponse {
  approved: number;
  skippedProtected: number;
  skippedIds: string[];
}

export interface ActionBatchDto {
  id: string;
  kind: string;
  description: string;
  messageCount: number;
  jobId: string | null;
  createdAt: string;
}

/** The sender's most approved outcome; the outcome fields are null when nothing is approved yet. */
export interface SenderPatternDto {
  topicLabel: string | null;
  needsAction: boolean | null;
  toBeDeleted: boolean | null;
  approvals: number;
  /** Share of `approvals` with this outcome, 0–1. */
  agreement: number;
  /** Messages without a suggestion. */
  remaining: number;
}

export interface FilterCandidateDto {
  from: string;
  listId: string | null;
}

export interface ApplyRestResponse {
  created: number;
  protectedAdjusted: number;
  batch: ActionBatchDto | null;
  filterCandidate: FilterCandidateDto;
}

/** Names of the two flag labels, from settings. */
export interface FlagLabels {
  action: string;
  delete: string;
}

export const DEFAULT_FLAG_LABELS: FlagLabels = { action: 'Needs action', delete: 'To be deleted' };

export const SOURCE_LABELS: Record<SuggestionSource, string> = {
  llm: 'LLM',
  derived: 'derived',
  memory: 'memory',
  sender_pattern: 'pattern',
};

export function percent(confidence: number): string {
  return `${Math.round(confidence * 100)}%`;
}

export function confidenceRange(
  group: Pick<ReviewGroupDto, 'confidenceMin' | 'confidenceMax'>,
): string {
  const min = percent(group.confidenceMin);
  const max = percent(group.confidenceMax);
  return min === max ? min : `${min}–${max}`;
}

/** "n analysed by the model · m derived · k from memory", leaving out zero parts after the first. */
export function groupOrigin(group: ReviewGroupDto): string {
  const parts = [`${group.llmCount} analysed by the model`];
  if (group.derivedCount) parts.push(`${group.derivedCount} derived`);
  if (group.memoryCount) parts.push(`${group.memoryCount} from memory`);
  return parts.join(' · ');
}

/** The card's label is new in Gmail when a member with that label says so. */
export function isNewGroupLabel(group: ReviewGroupDto): boolean {
  return group.members.some((m) => m.isNewLabel && m.topicLabel === group.topicLabel);
}

export function outcomeOf(group: ReviewGroupDto): ReviewOutcome {
  return {
    topicLabel: group.topicLabel,
    needsAction: group.needsAction,
    toBeDeleted: group.toBeDeleted,
  };
}

/** A pattern exists and there is mail left to apply it to. */
export function canApplyRest(pattern: SenderPatternDto | null): pattern is SenderPatternDto & {
  topicLabel: string;
} {
  return !!pattern?.topicLabel && pattern.remaining > 0;
}

/** What "Apply to rest of sender" will do, for the confirm dialog. */
export function patternSummary(
  pattern: SenderPatternDto & { topicLabel: string },
  labels: FlagLabels,
): string {
  const flags = [
    pattern.needsAction ? labels.action : null,
    pattern.toBeDeleted ? labels.delete : null,
  ].filter((f): f is string => !!f);
  const outcome = [`label "${pattern.topicLabel}"`, ...flags.map((f) => `"${f}"`)].join(', ');
  return (
    `Suggest ${outcome} for the ${pattern.remaining} remaining messages and apply it ` +
    `(${percent(pattern.agreement)} of ${pattern.approvals} approvals agree). ` +
    `Protected messages are not marked for deletion. ` +
    `A Gmail filter for this sender can be created in Rules (M6).`
  );
}

/** Why group approve left members pending. */
export function skippedMessage(count: number): string {
  return `${count} ${count === 1 ? 'member was' : 'members were'} skipped: another outcome than the card's, or a protected message marked for deletion. Review ${count === 1 ? 'it' : 'them'} one by one.`;
}

/**
 * Opens the edit dialog for a group or a member and emits `true` once something was saved. #120
 * implements it; until then the stub says it is coming.
 */
export interface ReviewEditDialog {
  editGroup(senderAddress: string, group: ReviewGroupDto): Observable<boolean>;
  editMember(suggestion: SuggestionDto): Observable<boolean>;
}

export const REVIEW_EDIT_DIALOG = new InjectionToken<ReviewEditDialog>('REVIEW_EDIT_DIALOG', {
  providedIn: 'root',
  factory: () => {
    const snackBar = inject(MatSnackBar);
    const comingSoon = () => {
      snackBar.open('Editing a suggestion is coming soon.', 'Dismiss', { duration: 4000 });
      return of(false);
    };
    return { editGroup: comingSoon, editMember: comingSoon };
  },
});
