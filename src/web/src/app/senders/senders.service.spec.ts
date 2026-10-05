import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import { QUIET_STATUSES } from '../core/error.interceptor';
import {
  DEFAULT_SENDER_QUERY,
  hasControlChars,
  normaliseAllowlistAddress,
  normaliseFetchTarget,
  parseSenderQuery,
  relativeTime,
  senderRow,
  SenderDto,
  SenderFetchStarted,
  SenderQuery,
  senderQueryParams,
} from './senders.models';
import { SendersService } from './senders.service';

describe('SendersService', () => {
  let service: SendersService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SendersService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list sends page, size, sort and dir, and the trimmed search', () => {
    service
      .list({ search: '  news ', page: 3, pageSize: 25, sort: 'lastSeen', dir: 'asc', kinds: [] })
      .subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/api/senders');
    expect(req.request.params.toString()).toBe(
      'page=3&pageSize=25&sort=lastSeen&dir=asc&search=news',
    );
    req.flush({ items: [], page: 3, pageSize: 25, total: 0 });
  });

  it('listNoisy sends the thresholds as a ratio and leaves out an empty dormant filter', () => {
    service
      .listNoisy({
        minMessages: 12,
        minUnreadPercent: 85,
        dormantDays: null,
        search: ' shop ',
        page: 2,
        pageSize: 50,
      })
      .subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/api/senders/noisy');
    expect(req.request.params.toString()).toBe(
      'minMessages=12&minUnreadRatio=0.85&page=2&pageSize=50&search=shop',
    );
    req.flush({ items: [], page: 2, pageSize: 50, total: 0 });
  });

  it('proposeNoisy and archiveSenders post the senders and keep a 422 out of the snackbar', () => {
    service
      .proposeNoisy({
        canonicalAddresses: ['a@example.com'],
        toBeDeleted: true,
        unsubscribe: false,
      })
      .subscribe();
    service.archiveSenders({ canonicalAddresses: ['a@example.com'] }).subscribe();
    const propose = http.expectOne('/api/senders/noisy/proposals');
    expect(propose.request.body).toEqual({
      canonicalAddresses: ['a@example.com'],
      toBeDeleted: true,
      unsubscribe: false,
    });
    expect(propose.request.context.get(QUIET_STATUSES)).toEqual([422]);
    const archive = http.expectOne('/api/senders/archive');
    expect(archive.request.method).toBe('POST');
    expect(archive.request.context.get(QUIET_STATUSES)).toEqual([422]);
    propose.flush({ created: 1, skippedProtected: 0, skippedAlreadySuggested: 0 });
    archive.flush({ id: 'job-1' });
  });

  it('list repeats kind once per selected kind and sends the unread sort', () => {
    service.list({ ...DEFAULT_SENDER_QUERY, sort: 'unread', kinds: ['bulk', 'mixed'] }).subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/api/senders');
    expect(req.request.params.get('sort')).toBe('unread');
    expect(req.request.params.getAll('kind')).toEqual(['bulk', 'mixed']);
    req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
  });

  it('list filters on the allowlist flag when asked', () => {
    service.list(DEFAULT_SENDER_QUERY, true).subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/api/senders');
    expect(req.request.params.get('allowlisted')).toBe('true');
    req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
  });

  it('setAllowlisted puts the flag on the encoded address', () => {
    service.setAllowlisted('a+b@example.com', false).subscribe();
    const req = http.expectOne('/api/senders/a%2Bb%40example.com/allowlist');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ allowlisted: false });
    req.flush({});
  });

  it('list leaves out an empty search', () => {
    service.list({ ...DEFAULT_SENDER_QUERY, search: '   ' }).subscribe();
    const req = http.expectOne((r) => r.url === '/api/senders');
    expect(req.request.params.has('search')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
  });

  it('fetchFromSender posts the target and tells a new job (202) from an active one (200)', () => {
    const results: SenderFetchStarted[] = [];
    service.fetchFromSender('example.com').subscribe((r) => results.push(r));
    const req = http.expectOne({ method: 'POST', url: '/api/fetch/sender' });
    expect(req.request.body).toEqual({ target: 'example.com' });
    req.flush({ jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    service.fetchFromSender('example.com').subscribe((r) => results.push(r));
    http
      .expectOne('/api/fetch/sender')
      .flush({ jobId: 'job-1' }, { status: 200, statusText: 'OK' });
    expect(results).toEqual([
      { jobId: 'job-1', created: true },
      { jobId: 'job-1', created: false },
    ]);
  });
});

describe('sender query params', () => {
  it('fall back to the defaults for missing or invalid values', () => {
    const bad = { page: '0', pageSize: '30', sort: 'name', dir: 'up' };
    expect(parseSenderQuery(convertToParamMap({}))).toEqual(DEFAULT_SENDER_QUERY);
    expect(parseSenderQuery(convertToParamMap(bad))).toEqual(DEFAULT_SENDER_QUERY);
    expect(parseSenderQuery(convertToParamMap({ page: '1.5' })).page).toBe(1);
  });

  it('keep only known kinds, once each, in display order', () => {
    const params = convertToParamMap({ kind: ['mixed', 'robot', 'human', 'mixed'] });
    expect(parseSenderQuery(params).kinds).toEqual(['human', 'mixed']);
    expect(senderQueryParams({ ...DEFAULT_SENDER_QUERY, kinds: ['bulk'] })['kind']).toEqual([
      'bulk',
    ]);
  });

  it('turn control characters in the search into spaces', () => {
    expect(parseSenderQuery(convertToParamMap({ search: 'a\tb\n' })).search).toBe('a b');
    expect(hasControlChars('a\u0085b')).toBe(true);
    expect(hasControlChars('a b')).toBe(false);
  });

  it('round-trip, leaving the defaults out of the URL', () => {
    const query: SenderQuery = {
      search: 'news',
      page: 2,
      pageSize: 100,
      sort: 'unread',
      dir: 'asc',
      kinds: ['human', 'unknown'],
    };
    const params = senderQueryParams(query);
    expect(parseSenderQuery(convertToParamMap(params))).toEqual(query);
    expect(Object.values(senderQueryParams(DEFAULT_SENDER_QUERY)).every((v) => v === null)).toBe(
      true,
    );
  });
});

describe('normaliseFetchTarget', () => {
  it.each([
    ['news@example.com', 'news@example.com'],
    ['  News@Example.COM ', 'news@example.com'],
    ['example.com', 'example.com'],
    ['@mail.example.com', 'mail.example.com'],
    ["o'brien+tag@example.co.uk", "o'brien+tag@example.co.uk"],
  ])('accepts %s as %s', (input, expected) => {
    expect(normaliseFetchTarget(input)).toBe(expected);
  });

  it.each([
    '',
    '   ',
    'com',
    '@com',
    'news@com',
    '@news@example.com',
    'news example.com',
    'news@exa_mple.com',
    '"news"@example.com',
    '-example.com',
    `${'a'.repeat(65)}@example.com`,
  ])('rejects %j', (input) => {
    expect(normaliseFetchTarget(input)).toBeNull();
  });
});

describe('normaliseAllowlistAddress', () => {
  it.each([
    ['News@Example.com', 'news@example.com'],
    ['  <news@example.com> ', 'news@example.com'],
  ])('accepts %j', (input, expected) => {
    expect(normaliseAllowlistAddress(input)).toBe(expected);
  });

  it.each([
    '',
    'example.com',
    '@example.com',
    '<news@example.com',
    'a@b@example.com',
    'a;b@example.com',
  ])('rejects %j', (input) => {
    expect(normaliseAllowlistAddress(input)).toBeNull();
  });
});

describe('senderRow', () => {
  it('rounds the unread share and treats an empty sender as 0%', () => {
    const counts = { analysedCount: 0, unreadCount: 2, totalCount: 3 };
    expect(senderRow(counts as SenderDto).unread).toBe(67);
    expect(senderRow({ ...counts, totalCount: 0 } as SenderDto).unread).toBe(0);
  });
});

describe('relativeTime', () => {
  const now = Date.parse('2026-01-10T12:00:00Z');

  it('picks the largest whole unit', () => {
    const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
    expect(relativeTime('2026-01-07T12:00:00Z', now)).toBe(format.format(-3, 'day'));
    expect(relativeTime('2026-01-10T10:00:00Z', now)).toBe(format.format(-2, 'hour'));
    expect(relativeTime('2026-01-10T11:59:50Z', now)).toBe(format.format(0, 'second'));
  });
});
