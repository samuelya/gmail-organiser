import { formatNumber } from '@angular/common';
import { ChartLinkCell, ChartTable } from '../core/charts/chart.models';
import {
  cleanSearch,
  DEFAULT_SENDER_QUERY,
  SenderDto,
  SenderQuery,
  senderQueryParams,
} from '../senders/senders.models';

/** How many senders the chart shows. */
export const TOP_SENDERS = 10;

/** Bar labels longer than this are cut, ending in an ellipsis. */
export const LABEL_MAX = 28;

/** The senders with the most mail, highest first. */
export const TOP_SENDERS_QUERY: Readonly<SenderQuery> = {
  ...DEFAULT_SENDER_QUERY,
  sort: 'total',
  dir: 'desc',
  page: 1,
  pageSize: TOP_SENDERS,
};

/** One bar: the sender's mail split into unread and read. */
export interface TopSender {
  /** The bar label: `name`, cut to `LABEL_MAX` characters. */
  label: string;
  /** `displayName`, or the address when there is none. */
  name: string;
  address: string;
  total: number;
  unread: number;
  read: number;
}

/** Cuts `text` to `max` characters, the last being an ellipsis; counts code points, not UTF-16 units. */
export function truncate(text: string, max = LABEL_MAX): string {
  const chars = Array.from(text);
  return chars.length <= max ? text : `${chars.slice(0, max - 1).join('')}…`;
}

/** The bars in the API's order; read is clamped at 0 should the counts ever disagree. */
export function topSenders(senders: readonly SenderDto[]): TopSender[] {
  return senders.map((s) => {
    const name = s.displayName?.trim() || s.address;
    return {
      label: truncate(name),
      name,
      address: s.address,
      total: s.totalCount,
      unread: s.unreadCount,
      read: Math.max(0, s.totalCount - s.unreadCount),
    };
  });
}

/** The senders list searched for one address: where a bar and its table row lead. */
export function senderLink(address: string): ChartLinkCell {
  const queryParams = senderQueryParams({ ...DEFAULT_SENDER_QUERY, search: cleanSearch(address) });
  return { text: address, link: '/senders', queryParams };
}

/** "The top 10 senders sent 1,240 of your 5,000 stored messages (25%)."; no share without a stored count. */
export function topSendersSummary(
  rows: readonly TopSender[],
  messagesStored: number | null,
  locale: string,
): string {
  if (rows.length === 0) return 'No senders yet.';
  const count = (n: number) => formatNumber(n, locale, '1.0-0');
  const sum = rows.reduce((total, r) => total + r.total, 0);
  const who = rows.length === 1 ? 'The top sender' : `The top ${rows.length} senders`;
  if (!messagesStored) return `${who} sent ${count(sum)} messages.`;
  // Sender stats and the stored count are kept up to date separately, so the sum can run ahead.
  const share = Math.min(100, Math.round((sum / messagesStored) * 100));
  return `${who} sent ${count(sum)} of your ${count(messagesStored)} stored messages (${share}%).`;
}

/** The same senders as a table; the sender cell links where its bar does. */
export function topSendersTable(rows: readonly TopSender[]): ChartTable {
  return {
    columns: ['Sender', 'Address', 'Total', 'Unread'],
    rows: rows.map((r) => [
      { ...senderLink(r.address), text: r.name },
      r.address,
      r.total,
      r.unread,
    ]),
  };
}
