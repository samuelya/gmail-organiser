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
  /** The address itself is allowlisted. */
  allowlisted: boolean;
  /** The sender's domain, or a parent domain, is in `protection.allowlistedDomains`. */
  allowlistedByDomain: boolean;
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
  { kind: 'ids'; ids: readonly string[] } | { kind: 'sender'; address: string } | { kind: 'all' };

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

/** How a sender can be (or was) unsubscribed from: one-click by the api, or a link / `mailto:` the user opens. */
export type UnsubscribeMethod = 'one_click' | 'link' | 'mailto';

/** `UnsubscribeInfoDto` from `GET /api/clean-up/senders/{address}/unsubscribe`. */
export interface UnsubscribeInfo {
  /** Null when none of the sender's newest messages offers a usable `List-Unsubscribe`. */
  method: UnsubscribeMethod | null;
  url: string | null;
  messageId: string | null;
  unsubscribedAt: string | null;
  unsubscribedVia: UnsubscribeMethod | null;
}

/** `UnsubscribeResultDto`: `httpStatus` is null when the sender's server never answered. */
export interface UnsubscribeResult {
  status: 'done' | 'failed';
  httpStatus: number | null;
}

/** The schemes each method may carry; anything else is treated as no unsubscribe header. */
const UNSUBSCRIBE_SCHEMES: Record<UnsubscribeMethod, readonly string[]> = {
  one_click: ['https:'],
  link: ['https:', 'http:'],
  mailto: ['mailto:'],
};

/** The info's URL when it parses and its scheme fits the method; null otherwise. */
export function unsubscribeUrl(info: UnsubscribeInfo | null): URL | null {
  if (!info?.method || !info.url) return null;
  try {
    const url = new URL(info.url);
    return UNSUBSCRIBE_SCHEMES[info.method].includes(url.protocol) ? url : null;
  } catch {
    return null;
  }
}

/** What the UI shows of an unsubscribe URL: the host, or the `mailto:` domain; never a path or token. */
export function unsubscribeHost(url: URL): string {
  if (url.protocol !== 'mailto:') return url.hostname;
  const raw = url.pathname.split(',')[0];
  let address = raw;
  try {
    address = decodeURIComponent(raw);
  } catch {
    // Sender-controlled header with a malformed escape: show the raw domain instead.
  }
  return address.slice(address.lastIndexOf('@') + 1).toLowerCase();
}

/** The snackbar after a one-click POST the sender's server did not accept. */
export function unsubscribeFailedMessage(result: UnsubscribeResult): string {
  const status = result.httpStatus === null ? '' : ` (HTTP ${result.httpStatus})`;
  return `Unsubscribe failed${status}; open the link instead`;
}
