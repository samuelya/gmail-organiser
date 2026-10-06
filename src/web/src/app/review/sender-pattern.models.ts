import { ActionBatchDto, FlagLabels, percent } from './review.models';

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

/** "Apply to rest of sender" created suggestions; protected messages are never marked for deletion. */
export function applyRestMessage(created: number, protectedAdjusted: number): string {
  const adjusted = protectedAdjusted
    ? `; ${protectedAdjusted} protected ${protectedAdjusted === 1 ? 'message is' : 'messages are'} not marked for deletion`
    : '';
  return `Created ${created} ${created === 1 ? 'suggestion' : 'suggestions'}${adjusted}.`;
}
