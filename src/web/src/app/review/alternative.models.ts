import type { LabelChange, ReviewGroupDto, ReviewStatus, SuggestionDto } from './review.models';
import { labelChangeText, percent } from './review.models';

/** A re-analysis result stored next to a suggestion (or, on a group, its listed members'); nothing changes until accepted. */
export interface SuggestionAlternativeDto {
  topicLabel: string;
  documentTypeLabel: string | null;
  /** As on the suggestion, for the alternative's topic label. */
  replaceLabels: string[];
  labelChange: LabelChange;
  needsAction: boolean;
  toBeDeleted: boolean;
  unsubscribeSuggested: boolean;
  confidence: number;
  reason: string;
  promptVersion: string | null;
  model: string | null;
  createdAt: string;
  /** Group only: the members' alternatives disagree; the fields show the most common one. */
  mixed: boolean;
  /** Listed members with an alternative (1 for a suggestion). */
  count: number;
}

/** A group in an alternative decision; `status` is the viewed tab's (pending when left out). */
export interface AlternativeGroupRef {
  senderAddress: string;
  groupKey: string;
  status: ReviewStatus;
}

/** `POST /api/review/alternatives/accept|discard`: suggestions by id and every suggestion of the named groups. */
export interface AlternativeDecisionRequest {
  suggestionIds?: string[];
  groups?: AlternativeGroupRef[];
}

export interface AlternativeDecisionResponse {
  accepted: number;
  discarded: number;
  /** Left alone: part of an active apply batch. */
  skipped: number;
}

/** "Use new" accepts the alternative, "Keep current" discards it. */
export type AlternativeDecision = 'accept' | 'discard';

/** What a "Use new" or "Keep current" names: one member, or a whole group card. */
export type AlternativeTarget = { member: SuggestionDto } | { group: ReviewGroupDto };

/** The current outcome the comparison shows; a group's confidence is a range. */
export interface CompareValues {
  topicLabel: string;
  documentTypeLabel: string | null;
  labelChange: LabelChange;
  replaceLabels: string[];
  needsAction: boolean;
  toBeDeleted: boolean;
  confidenceMin: number;
  confidenceMax: number;
}

/** Which compared fields differ between the current outcome and the alternative. */
export interface AlternativeDiff {
  topicLabel: boolean;
  documentType: boolean;
  labelChange: boolean;
  needsAction: boolean;
  toBeDeleted: boolean;
  confidence: boolean;
}

export function memberValues(m: SuggestionDto): CompareValues {
  return { ...m, confidenceMin: m.confidence, confidenceMax: m.confidence };
}

/** Confidence differs when the alternative's shown percentage lies outside the current (rounded) range. */
export function alternativeDiff(
  current: CompareValues,
  alternative: SuggestionAlternativeDto,
): AlternativeDiff {
  const rounded = (c: number) => Math.round(c * 100);
  const confidence = rounded(alternative.confidence);
  return {
    topicLabel: current.topicLabel !== alternative.topicLabel,
    documentType: (current.documentTypeLabel ?? null) !== (alternative.documentTypeLabel ?? null),
    labelChange: labelChangeText(current) !== labelChangeText(alternative),
    needsAction: current.needsAction !== alternative.needsAction,
    toBeDeleted: current.toBeDeleted !== alternative.toBeDeleted,
    confidence:
      confidence < rounded(current.confidenceMin) || confidence > rounded(current.confidenceMax),
  };
}

/** "Prompt v4 · model-x" for the alternative's header; empty when neither is known. */
export function alternativeSource(alternative: SuggestionAlternativeDto): string {
  return [
    alternative.promptVersion ? `Prompt ${alternative.promptVersion}` : null,
    alternative.model,
  ]
    .filter((part) => !!part)
    .join(' · ');
}

export function alternativeConfidence(alternative: SuggestionAlternativeDto): string {
  return percent(alternative.confidence);
}

/**
 * The request for a member or a group card. A message analysed on its own has no group key, so it goes
 * by its suggestion id.
 */
export function alternativeRequest(
  target: AlternativeTarget,
  senderAddress: string,
  status: ReviewStatus,
): AlternativeDecisionRequest {
  if ('member' in target) return { suggestionIds: [target.member.id] };
  const g = target.group;
  if (g.groupKey === null) return { suggestionIds: g.members.slice(0, 1).map((m) => m.id) };
  return { groups: [{ senderAddress, groupKey: g.groupKey, status }] };
}

/** Accepting re-pends an approved or applied suggestion: it must be approved and applied again. */
export function repends(target: AlternativeTarget): boolean {
  const members = 'member' in target ? [target.member] : target.group.members;
  return members.some(
    (m) => !!m.alternative && (m.status === 'approved' || m.status === 'applied'),
  );
}

/** The snackbar after a decision. */
export function alternativeMessage(
  decision: AlternativeDecision,
  r: AlternativeDecisionResponse,
): string {
  const n = decision === 'accept' ? r.accepted : r.discarded;
  const done =
    decision === 'accept'
      ? `Used the new result for ${n} ${n === 1 ? 'suggestion' : 'suggestions'}.`
      : `Kept the current result for ${n} ${n === 1 ? 'suggestion' : 'suggestions'}.`;
  return r.skipped ? `${done} ${r.skipped} skipped: part of an apply that is still running.` : done;
}

/** The note after "Use new" on an approved or applied suggestion. */
export const PENDING_AGAIN_NOTE =
  'The new result is pending again: approve it and apply it to change Gmail.';
