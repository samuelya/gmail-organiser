import { ActionBatchDto } from '../review/review.models';

/** `CleanupSummaryDto`: stored messages carrying the delete label; all 0 when the label does not exist. */
export interface CleanupSummary {
  messages: number;
  senders: number;
  /** Of `messages`, those Delete skips unless asked to include them. */
  protected: number;
}

/** `CleanupSenderDto` from `GET /api/clean-up/senders`. */
export interface CleanupSender {
  address: string;
  displayName: string | null;
  count: number;
  protectedCount: number;
  oldestAt: string | null;
  newestAt: string | null;
  allowlisted: boolean;
}

/** `CleanupMessageDto`: `protectedReason` is null when Delete does not skip the message. */
export interface CleanupMessage {
  id: string;
  subject: string | null;
  snippet: string | null;
  internalDate: string;
  sizeEstimate: number;
  inInbox: boolean;
  protectedReason: string | null;
}

/** `CleanupSelectionRequest`: exactly one of `messageIds`, `senderAddress` or `all`. */
export interface CleanupSelectionRequest {
  messageIds?: string[];
  senderAddress?: string;
  all?: boolean;
  /** Delete only; Unmark always includes protected messages. */
  includeProtected?: boolean;
}

/** `CleanupBatchDto`: the History batch, the messages its job will change and the protected ones left alone. */
export interface CleanupBatch {
  batch: ActionBatchDto;
  queued: number;
  skippedProtected: number;
}

/** What an action applies to: ticked rows, a whole sender or every delete-labelled message. */
export type CleanupSelection =
  | { kind: 'ids'; ids: readonly string[] }
  | { kind: 'sender'; address: string }
  | { kind: 'all' };

/** The selection as the API takes it. */
export function selectionRequest(
  selection: CleanupSelection,
  includeProtected?: boolean,
): CleanupSelectionRequest {
  const request: CleanupSelectionRequest =
    selection.kind === 'ids'
      ? { messageIds: [...selection.ids] }
      : selection.kind === 'sender'
        ? { senderAddress: selection.address }
        : { all: true };
  return includeProtected === undefined ? request : { ...request, includeProtected };
}

/** The job type of both clean-up actions (`CleanUpJobTypes.Actions`). */
export const CLEANUP_JOB = 'cleanup_actions';
export const CLEANUP_SENDER_PAGE_SIZE = 25;
export const CLEANUP_MESSAGE_PAGE_SIZE = 50;

/** What the delete confirm dialog states. */
export interface DeleteConfirmData {
  /** The delete label's name, from settings. */
  labelName: string;
  /** "the selected messages", the sender, or everything. */
  scope: string;
  count: number;
  protectedCount: number;
}

/** How many messages a Delete moves to Trash. */
export function trashCount(data: DeleteConfirmData, includeProtected: boolean): number {
  return includeProtected ? data.count : Math.max(0, data.count - data.protectedCount);
}

export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

/** The snackbar after a batch was queued. */
export function queuedMessage(verb: string, result: CleanupBatch): string {
  const skipped = result.skippedProtected
    ? `; ${plural(result.skippedProtected, 'protected message')} skipped`
    : '';
  return `${verb} ${plural(result.queued, 'message')}${skipped}.`;
}

/** `1234567` → "1.2 MB". */
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}
