import { HttpErrorResponse } from '@angular/common/http';
import { plural } from '../clean-up/clean-up.models';
import { ConfirmDialogData } from '../core/confirm-dialog';
import { sameLabels } from './extra-labels-field.component';
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
  /** The outcome's additional labels; missing is none. */
  extraLabels?: string[];
}

/**
 * `POST …/senders/{address}/apply-rest`: each value overrides the pattern's; `documentTypeLabel: ""`
 * sets none, `extraLabels: []` clears them.
 */
export interface ApplyRestRequest {
  documentTypeLabel?: string;
  extraLabels?: string[];
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
 * pattern has no type. The additional labels are sent only when `extraLabels` differs from the
 * pattern's, so the pattern's set is otherwise kept as approved.
 */
export function applyRestRequest(
  pattern: SenderPatternDto,
  parent: string | null,
  extraLabels?: readonly string[],
): ApplyRestRequest {
  const type = parent && !pattern.documentTypeLabel ? { documentTypeLabel: '' } : {};
  return extraLabels && !sameLabels(extraLabels, pattern.extraLabels ?? [])
    ? { ...type, extraLabels: [...extraLabels] }
    : type;
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
  const extras = pattern.extraLabels?.length
    ? [
        `additional ${pattern.extraLabels.length === 1 ? 'label' : 'labels'} ` +
          pattern.extraLabels.map((l) => `"${l}"`).join(', '),
      ]
    : [];
  const outcome = [
    `label "${pattern.topicLabel}"`,
    ...type,
    ...flags.map((f) => `"${f}"`),
    ...extras,
  ].join(', ');
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

/** The confirm for applying every sender's `n` approved suggestions. */
export function applyAllConfirm(n: number): ConfirmDialogData {
  return {
    title: 'Apply all approved suggestions?',
    message:
      `${plural(n, 'approved suggestion')} from every sender ${n === 1 ? 'is' : 'are'} applied ` +
      `to Gmail. You can undo the batch from History.`,
    confirm: 'Apply all',
  };
}

/**
 * Apply all answers 409 when no approved suggestion is applicable: what is still counted as approved
 * has a label Gmail refuses. Null for any other error, which the error interceptor already showed.
 */
export function applyAllFailure(error: unknown): string | null {
  return error instanceof HttpErrorResponse && error.status === 409
    ? 'Nothing to apply: the approved suggestions left have labels Gmail refuses. ' +
        'Edit their labels, then apply again.'
    : null;
}
