import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import {
  DEFAULT_SENDER_QUERY,
  normaliseFetchTarget,
  parseSenderQuery,
  relativeTime,
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
      .list({ search: '  news ', page: 3, pageSize: 25, sort: 'lastSeen', dir: 'asc' })
      .subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url === '/api/senders');
    expect(req.request.params.toString()).toBe(
      'page=3&pageSize=25&sort=lastSeen&dir=asc&search=news',
    );
    req.flush({ items: [], page: 3, pageSize: 25, total: 0 });
  });

  it('list leaves out an empty search', () => {
    service.list({ ...DEFAULT_SENDER_QUERY, search: '   ' }).subscribe();
    const req = http.expectOne((r) => r.url === '/api/senders');
    expect(req.request.params.has('search')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
  });

  it('fetchFromSender posts the target', () => {
    let id = '';
    service.fetchFromSender('example.com').subscribe((r) => (id = r.jobId));
    const req = http.expectOne({ method: 'POST', url: '/api/fetch/sender' });
    expect(req.request.body).toEqual({ target: 'example.com' });
    req.flush({ jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    expect(id).toBe('job-1');
  });
});

describe('sender query params', () => {
  it('fall back to the defaults for missing or invalid values', () => {
    const bad = { page: '0', pageSize: '30', sort: 'name', dir: 'up' };
    expect(parseSenderQuery(convertToParamMap({}))).toEqual(DEFAULT_SENDER_QUERY);
    expect(parseSenderQuery(convertToParamMap(bad))).toEqual(DEFAULT_SENDER_QUERY);
    expect(parseSenderQuery(convertToParamMap({ page: '1.5' })).page).toBe(1);
  });

  it('round-trip, leaving the defaults out of the URL', () => {
    const query = { search: 'news', page: 2, pageSize: 100, sort: 'address', dir: 'asc' } as const;
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

describe('relativeTime', () => {
  const now = Date.parse('2026-01-10T12:00:00Z');

  it('picks the largest whole unit', () => {
    const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
    expect(relativeTime('2026-01-07T12:00:00Z', now)).toBe(format.format(-3, 'day'));
    expect(relativeTime('2026-01-10T10:00:00Z', now)).toBe(format.format(-2, 'hour'));
    expect(relativeTime('2026-01-10T11:59:50Z', now)).toBe(format.format(0, 'second'));
  });
});
