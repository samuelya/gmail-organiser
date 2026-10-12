import { SenderDto } from '../senders/senders.models';
import {
  LABEL_MAX,
  TOP_SENDERS_QUERY,
  senderLink,
  topSenders,
  topSendersSummary,
  topSendersTable,
  truncate,
} from './top-senders';

const sender = (over: Partial<SenderDto> = {}): SenderDto => ({
  address: 'news@example.com',
  domain: 'example.com',
  displayName: 'Example News',
  totalCount: 100,
  analysedCount: 0,
  appliedCount: 0,
  lastSeenAt: null,
  allowlisted: false,
  allowlistedByDomain: false,
  activeFetchJob: null,
  unsubscribedAt: null,
  canonicalAddress: 'news@example.com',
  canonicalDomain: 'example.com',
  isRelay: false,
  kind: 'bulk',
  unreadCount: 40,
  repliedCount: 0,
  firstSeenAt: null,
  ...over,
});

describe('top senders', () => {
  it('asks for the ten senders with the most mail', () => {
    expect(TOP_SENDERS_QUERY).toMatchObject({
      sort: 'total',
      dir: 'desc',
      page: 1,
      pageSize: 10,
      search: '',
    });
  });

  it('splits each sender into unread and read, keeping the API order', () => {
    const rows = topSenders([
      sender({ address: 'b@example.com', displayName: 'B', totalCount: 50, unreadCount: 5 }),
      sender({ address: 'a@example.com', displayName: null, totalCount: 80, unreadCount: 80 }),
      sender({ address: 'c@example.com', displayName: '  ', totalCount: 3, unreadCount: 4 }),
    ]);
    expect(rows.map((r) => [r.label, r.name, r.address, r.total, r.unread, r.read])).toEqual([
      ['B', 'B', 'b@example.com', 50, 5, 45],
      ['a@example.com', 'a@example.com', 'a@example.com', 80, 80, 0],
      ['c@example.com', 'c@example.com', 'c@example.com', 3, 4, 0],
    ]);
  });

  it('truncates long labels to 28 characters with an ellipsis and keeps the full name', () => {
    const long = 'A very long synthetic sender display name';
    const [row] = topSenders([sender({ displayName: long })]);
    expect(row.label).toBe('A very long synthetic sende…');
    expect(Array.from(row.label)).toHaveLength(LABEL_MAX);
    expect(row.name).toBe(long);
    expect(truncate('x'.repeat(LABEL_MAX))).toBe('x'.repeat(LABEL_MAX));
    expect(truncate('😀'.repeat(30), 3)).toBe('😀😀…');
  });

  it('links to the senders list searched for the address', () => {
    expect(senderLink('a+b@example.com')).toEqual({
      text: 'a+b@example.com',
      link: '/senders',
      queryParams: expect.objectContaining({ search: 'a+b@example.com', page: null }),
    });
    expect(senderLink(' a@example.com\t').queryParams).toEqual(
      expect.objectContaining({ search: 'a@example.com' }),
    );
  });

  it('summarises the senders’ share of the stored messages', () => {
    const rows = topSenders([
      sender({ totalCount: 1000 }),
      sender({ address: 'b@example.com', totalCount: 240 }),
    ]);
    expect(topSendersSummary(rows, 5000, 'en-US')).toBe(
      'The top 2 senders sent 1,240 of your 5,000 stored messages (25%).',
    );
    expect(topSendersSummary(rows, 0, 'en-US')).toBe('The top 2 senders sent 1,240 messages.');
    expect(topSendersSummary(rows, null, 'en-US')).toBe('The top 2 senders sent 1,240 messages.');
    expect(topSendersSummary(rows.slice(0, 1), 3000, 'en-US')).toBe(
      'The top sender sent 1,000 of your 3,000 stored messages (33%).',
    );
    expect(topSendersSummary([], 10, 'en-US')).toBe('No senders yet.');
    expect(topSendersSummary(rows, 1000, 'en-US')).toBe(
      'The top 2 senders sent 1,240 of your 1,000 stored messages (100%).',
    );
  });

  it('builds the table with the full name, address and a link on the sender', () => {
    const table = topSendersTable(topSenders([sender({ totalCount: 7, unreadCount: 2 })]));
    expect(table.columns).toEqual(['Sender', 'Address', 'Total', 'Unread']);
    expect(table.rows).toEqual([
      [
        {
          text: 'Example News',
          link: '/senders',
          queryParams: expect.objectContaining({ search: 'news@example.com' }),
        },
        'news@example.com',
        7,
        2,
      ],
    ]);
  });
});
