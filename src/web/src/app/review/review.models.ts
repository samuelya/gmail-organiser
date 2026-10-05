import { InjectionToken } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CreateExternalReviewsRequest,
  ExternalReviewDto,
  MAX_CLAUDE_TARGETS,
} from '../core/claude.models';
import type { SuggestionAlternativeDto } from './alternative.models';
import { LabelDto } from './labels.models';

/** The statuses `GET /api/review/senders` and `…/senders/{address}` filter by. Applied is read-only. */
export type ReviewStatus = 'pending' | 'approved' | 'rejected' | 'applied';
export const REVIEW_STATUSES: readonly { value: ReviewStatus; label: string }[] = [
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
  { value: 'applied', label: 'Applied' },
];

/** `SuggestionStatus` as the API writes it. */
export type SuggestionStatus = ReviewStatus;
/** `SuggestionSource` as the API writes it. */
export type SuggestionSource = 'llm' | 'derived' | 'memory' | 'sender_pattern';

/** What applying a suggestion does to the message's own labels, as the API writes it. */
export type LabelChange = 'none' | 'keep' | 'add' | 'move' | 'relabel';

export const SENDER_PAGE_SIZE = 25;
/** The API's `ReviewQuery.MaxGroupPageSize` is 50. */
export const GROUP_PAGE_SIZE = 20;
export const MAX_GROUP_PAGE_SIZE = 50;
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
  /** Current labels (by name) applying removes; never the topic label. */
  replaceLabels: string[];
  /** The message's personal label names; empty when Gmail is not reachable. */
  currentLabels: string[];
  labelChange: LabelChange;
  /** The newest not-cancelled Claude review item for this suggestion alone. */
  claudeReview?: ExternalReviewDto | null;
  /** Worth a Claude review by the settings; a hint only. */
  suggestedForClaude?: boolean;
  /** The second label apply adds next to the topic label; null when none. */
  documentTypeLabel: string | null;
  /** Gmail did not have `documentTypeLabel` when it was suggested or edited. */
  documentTypeIsNew: boolean;
  /** The re-analysis result waiting next to it; null when none. */
  alternative?: SuggestionAlternativeDto | null;
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
  /** Union of the listed members' replaced labels. */
  replaceLabels: string[];
  labelChange: LabelChange;
  /** The newest not-cancelled Claude review item for the group. */
  claudeReview?: ExternalReviewDto | null;
  /** Any member is worth a Claude review. */
  suggestedForClaude?: boolean;
  /** The shown outcome's document-type label; null when it has none. */
  documentTypeLabel: string | null;
  /** A listed member with the shown document-type label would create it in Gmail. */
  documentTypeIsNew: boolean;
  /** The listed members' re-analysis result (`mixed` when they disagree); null when none has one. */
  alternative?: SuggestionAlternativeDto | null;
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

/** The card's outcome with its document type; group approve takes only the members with exactly this outcome. */
export interface GroupOutcome extends ReviewOutcome {
  /** Null approves only members without one. */
  documentTypeLabel: string | null;
}

/**
 * `PUT /api/review/suggestions/{id}`; `replaceLabels` left out keeps the replaced labels as they are,
 * `documentTypeLabel` left out keeps the document type and `""` clears it.
 */
export interface EditSuggestionRequest extends ReviewOutcome {
  replaceLabels?: string[];
  documentTypeLabel?: string;
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

/** `apply_actions`, the job type an apply batch enqueues. */
export const APPLY_JOB = 'apply_actions';

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
  /** The most common document-type label among the outcome's approvals. */
  documentTypeLabel: string | null;
}

/** `POST …/senders/{address}/apply-rest`: each value overrides the pattern's; `documentTypeLabel: ""` sets none. */
export interface ApplyRestRequest {
  documentTypeLabel?: string;
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

export function outcomeOf(group: ReviewGroupDto): GroupOutcome {
  return {
    topicLabel: group.topicLabel,
    needsAction: group.needsAction,
    toBeDeleted: group.toBeDeleted,
    documentTypeLabel: group.documentTypeLabel,
  };
}

/** Mirrors the API's `DocumentTypePath.MaxDepth`: a document type is 1 to 3 levels under the parent. */
export const MAX_DOCUMENT_TYPE_DEPTH = 3;
/** Gmail's label nesting limit, as `labelPathError` checks it. */
const LABEL_MAX_SEGMENTS = 5;

/**
 * Mirrors the API's `DocumentTypePath.MaxDepthUnder`: `MAX_DOCUMENT_TYPE_DEPTH`, fewer when the
 * parent's own levels leave less room within Gmail's five, never below 1.
 */
export function documentTypeMaxDepth(parent: string): number {
  const room = LABEL_MAX_SEGMENTS - parent.trim().split('/').length;
  return Math.min(Math.max(room, 1), MAX_DOCUMENT_TYPE_DEPTH);
}

/** `documentTypeMaxDepth` in words, "1 level" or "1 to n levels", as the API words it. */
export function documentTypeLevels(parent: string): string {
  const depth = documentTypeMaxDepth(parent);
  return depth === 1 ? '1 level' : `1 to ${depth} levels`;
}

/** The typed document type with each segment trimmed (`Invoice / Paid` is `Invoice/Paid`). */
export function normaliseDocumentType(text: string): string {
  return text.trim()
    ? text
        .split('/')
        .map((s) => s.trim())
        .join('/')
    : '';
}

/**
 * The user labels 1 to `documentTypeMaxDepth(parent)` levels under the document-type `parent`, as the
 * path below it (`Utilities`, `Utilities/Electricity`), deduplicated case-insensitively and
 * ordinal-sorted so a type comes before its children.
 */
export function documentTypeOptions(labels: readonly LabelDto[], parent: string): string[] {
  const prefix = `${parent}/`.toLowerCase();
  const depth = documentTypeMaxDepth(parent);
  const byKey = new Map<string, string>();
  for (const l of labels) {
    if (l.type !== 'user' || !l.name.toLowerCase().startsWith(prefix)) continue;
    const path = l.name.slice(prefix.length);
    const segments = path.split('/');
    if (segments.length > depth || segments.some((s) => !s.trim())) continue;
    if (!byKey.has(path.toLowerCase())) byKey.set(path.toLowerCase(), path);
  }
  return [...byKey.values()].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/**
 * The document-type label for `text` under `parent`: `""` for none, `<parent>/<path>` for 1 to
 * `documentTypeMaxDepth(parent)` trimmed segments, `null` when deeper or a segment is empty.
 */
export function toDocumentTypeLabel(parent: string, text: string): string | null {
  const path = normaliseDocumentType(text);
  if (!path) return '';
  const segments = path.split('/');
  if (segments.length > documentTypeMaxDepth(parent) || segments.some((s) => !s)) return null;
  return `${parent}/${path}`;
}

/**
 * The apply-rest body: a pattern's own type is never re-sent, so the server keeps it as approved
 * even when the parent has changed since; `""` only says "none" when the parent is on and the
 * pattern has no type.
 */
export function applyRestRequest(
  pattern: SenderPatternDto,
  parent: string | null,
): ApplyRestRequest {
  return parent && !pattern.documentTypeLabel ? { documentTypeLabel: '' } : {};
}

/** A pattern exists and there is mail left to apply it to. */
export function canApplyRest(pattern: SenderPatternDto | null): pattern is SenderPatternDto & {
  topicLabel: string;
} {
  return !!pattern?.topicLabel && pattern.remaining > 0;
}

/**
 * What "Apply to rest of sender" will do, for the confirm dialog. The pattern's type is named
 * whatever the settings say, since the server applies it either way.
 */
export function patternSummary(
  pattern: SenderPatternDto & { topicLabel: string },
  labels: FlagLabels,
): string {
  const flags = [
    pattern.needsAction ? labels.action : null,
    pattern.toBeDeleted ? labels.delete : null,
  ].filter((f): f is string => !!f);
  const type = pattern.documentTypeLabel ? [`document type "${pattern.documentTypeLabel}"`] : [];
  const outcome = [`label "${pattern.topicLabel}"`, ...type, ...flags.map((f) => `"${f}"`)].join(
    ', ',
  );
  return (
    `Suggest ${outcome} for the ${pattern.remaining} remaining messages and apply it ` +
    `(${percent(pattern.agreement)} of ${pattern.approvals} approvals agree). ` +
    `Protected messages are not marked for deletion. ` +
    `A Gmail filter for this sender can be created afterwards.`
  );
}

/** The chip text for a label change; `null` when nothing changes (`none`). */
export function labelChangeText(
  change: Pick<ReviewGroupDto, 'labelChange' | 'topicLabel' | 'replaceLabels'>,
): string | null {
  switch (change.labelChange) {
    case 'keep':
      return `Keeps ${change.topicLabel}`;
    case 'add':
      return `Adds ${change.topicLabel}`;
    case 'move':
      return `Moves ${change.replaceLabels.join(', ')} → ${change.topicLabel}`;
    case 'relabel':
      return `Relabels ${change.replaceLabels.join(', ')} → ${change.topicLabel}`;
    default:
      return null;
  }
}

/** Why group approve left members pending. */
export function skippedMessage(count: number): string {
  return `${count} ${count === 1 ? 'member was' : 'members were'} skipped: another outcome than the card's, or a protected message marked for deletion. Review ${count === 1 ? 'it' : 'them'} one by one.`;
}

/** Opens the edit dialog for a group or a member and emits `true` once something was saved. */
export interface ReviewEditDialog {
  editGroup(senderAddress: string, group: ReviewGroupDto): Observable<boolean>;
  editMember(suggestion: SuggestionDto): Observable<boolean>;
}

/** Provided by the review route (`MatReviewEditDialog`). */
export const REVIEW_EDIT_DIALOG = new InjectionToken<ReviewEditDialog>('REVIEW_EDIT_DIALOG');

/** The members an edit changes: every listed one not yet applied. */
export function editableMembers(members: readonly SuggestionDto[]): SuggestionDto[] {
  return members.filter((m) => m.status !== 'applied');
}

/**
 * `PUT /api/review/suggestions/{id}` for one member; a protected message is never marked for deletion.
 * `replaceLabels` (left out when unchanged) is narrowed to the labels the member carries: the API rejects others.
 */
export function editRequest(
  member: SuggestionDto,
  outcome: ReviewOutcome,
  replaceLabels?: readonly string[],
  documentTypeLabel?: string,
): EditSuggestionRequest {
  const request: EditSuggestionRequest = {
    topicLabel: outcome.topicLabel.trim(),
    needsAction: outcome.needsAction,
    toBeDeleted: outcome.toBeDeleted && !member.protected,
  };
  if (documentTypeLabel !== undefined) request.documentTypeLabel = documentTypeLabel;
  if (replaceLabels) {
    const carried = new Set(member.currentLabels.map((l) => l.toLowerCase()));
    request.replaceLabels = replaceLabels.filter((l) => carried.has(l.toLowerCase()));
  }
  return request;
}

/** The current labels the edit dialog lists for these members: their union, in first-seen order. */
export function currentLabelsOf(members: readonly SuggestionDto[]): string[] {
  return [...new Set(members.flatMap((m) => m.currentLabels))];
}

/** Whether every member carrying `label` replaces it (`true`), none does (`false`) or only some (`null`). */
export function replaceState(members: readonly SuggestionDto[], label: string): boolean | null {
  const key = label.toLowerCase();
  const carrying = members.filter((m) => m.currentLabels.some((l) => l.toLowerCase() === key));
  const replacing = carrying.filter((m) => m.replaceLabels.some((l) => l.toLowerCase() === key));
  return replacing.length === 0 ? false : replacing.length === carrying.length ? true : null;
}

/**
 * The member's replaced labels after the dialog's decisions (label → replace), else `undefined` when they stay
 * as they are. A label without a decision keeps the member's own choice, so a group edit never adds a removal
 * the user did not tick.
 */
export function decidedReplaceLabels(
  member: SuggestionDto,
  decisions: ReadonlyMap<string, boolean>,
): string[] | undefined {
  const key = (l: string) => l.toLowerCase();
  const decided = new Map([...decisions].map(([l, replace]) => [key(l), replace]));
  const carried = new Set(member.currentLabels.map(key));
  const own = new Set(member.replaceLabels.map(key).filter((l) => carried.has(l)));
  const next = member.currentLabels.filter((l) => decided.get(key(l)) ?? own.has(key(l)));
  const same = next.length === own.size && next.every((l) => own.has(key(l)));
  return same ? undefined : next;
}

/** What a group card sends to Claude and shows: a message analysed on its own is its one suggestion. */
export interface ClaudeCardTarget {
  request: CreateExternalReviewsRequest;
  review: ExternalReviewDto | null;
}

export function claudeCardTarget(
  senderAddress: string,
  group: ReviewGroupDto,
): ClaudeCardTarget | null {
  if (group.groupKey !== null) {
    return {
      request: { groups: [{ senderAddress, groupKey: group.groupKey }] },
      review: group.claudeReview ?? null,
    };
  }
  const only = group.members[0];
  return only ? { request: { suggestionIds: [only.id] }, review: only.claudeReview ?? null } : null;
}

/**
 * The sender's pending groups (as `GET …/senders/{address}?status=pending` lists them) as one request,
 * largest first and at most the API's limit; `null` when there is nothing to send.
 */
export function pendingClaudeRequest(
  senderAddress: string,
  groups: readonly ReviewGroupDto[],
): CreateExternalReviewsRequest | null {
  const request: Required<Pick<CreateExternalReviewsRequest, 'suggestionIds' | 'groups'>> = {
    suggestionIds: [],
    groups: [],
  };
  for (const g of groups.slice(0, MAX_CLAUDE_TARGETS)) {
    if (g.groupKey !== null) request.groups.push({ senderAddress, groupKey: g.groupKey });
    else if (g.members[0]) request.suggestionIds.push(g.members[0].id);
  }
  return request.groups.length || request.suggestionIds.length ? request : null;
}

/**
 * Puts a changed Claude item on the row it belongs to. A cancelled item clears the row only when it
 * is the one shown; an item older than the one shown is ignored. `accepted` says the item just became
 * accepted, so the suggestions changed too.
 */
export function patchClaudeReview(
  detail: ReviewSenderDetailDto,
  item: ExternalReviewDto,
): { detail: ReviewSenderDetailDto; accepted: boolean } {
  let accepted = false;
  const next = (
    held: ExternalReviewDto | null | undefined,
  ): ExternalReviewDto | null | undefined => {
    if (held && held.id !== item.id && held.createdAt > item.createdAt) return held;
    if (item.status === 'cancelled') return held?.id === item.id ? null : held;
    accepted ||= item.resolution === 'accepted_claude' && held?.resolution !== 'accepted_claude';
    return item;
  };
  const sameSender = detail.sender.address.toLowerCase() === item.senderAddress.toLowerCase();
  let changed = false;
  const groups = detail.groups.map((g) => {
    if (item.targetType === 'group') {
      if (!sameSender || g.groupKey === null || g.groupKey !== item.groupKey) return g;
      changed = true;
      return { ...g, claudeReview: next(g.claudeReview) };
    }
    if (!g.members.some((m) => m.id === item.suggestionId)) return g;
    changed = true;
    return {
      ...g,
      members: g.members.map((m) =>
        m.id === item.suggestionId ? { ...m, claudeReview: next(m.claudeReview) } : m,
      ),
    };
  });
  return { detail: changed ? { ...detail, groups } : detail, accepted };
}
