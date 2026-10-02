import { ParamMap, Params } from '@angular/router';
import { JobDto } from '../core/jobs.models';
import { SortDirection } from '../core/paging.models';

/** `SenderDto` from `GET /api/senders`. */
export interface SenderDto {
  address: string;
  domain: string;
  displayName: string | null;
  totalCount: number;
  analysedCount: number;
  appliedCount: number;
  lastSeenAt: string | null;
  allowlisted: boolean;
  /** The queued, running or paused `sender_fetch` job targeting this address or its domain. */
  activeFetchJob: JobDto | null;
}

export type SenderSort = 'total' | 'lastSeen' | 'address' | 'analysed';

/** The senders list request; also the page's URL query params. */
export interface SenderQuery {
  search: string;
  page: number;
  pageSize: number;
  sort: SenderSort;
  dir: SortDirection;
}

/** `sender_fetch`, the job type `POST /api/fetch/sender` enqueues. */
export const SENDER_FETCH_JOB = 'sender_fetch';

export const PAGE_SIZES: readonly number[] = [25, 50, 100];
/** The API's search limit. */
export const MAX_SEARCH_LENGTH = 200;

export const DEFAULT_SENDER_QUERY: Readonly<SenderQuery> = {
  search: '',
  page: 1,
  pageSize: 50,
  sort: 'total',
  dir: 'desc',
};

const SORTS: readonly SenderSort[] = ['total', 'lastSeen', 'address', 'analysed'];
/** Keeps a hand-edited URL inside the API's page limit, so it never answers 400. */
const MAX_PAGE = 1_000_000;

/** Reads the query from URL params; anything missing or invalid falls back to the default. */
export function parseSenderQuery(params: ParamMap): SenderQuery {
  const d = DEFAULT_SENDER_QUERY;
  const page = Number(params.get('page'));
  const pageSize = Number(params.get('pageSize'));
  const sort = params.get('sort') as SenderSort | null;
  const dir = params.get('dir');
  return {
    search: (params.get('search') ?? '').trim().slice(0, MAX_SEARCH_LENGTH),
    page: Number.isInteger(page) && page >= 1 && page <= MAX_PAGE ? page : d.page,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : d.pageSize,
    sort: sort && SORTS.includes(sort) ? sort : d.sort,
    dir: dir === 'asc' || dir === 'desc' ? dir : d.dir,
  };
}

/** URL query params for a query; defaults are left out (`null` removes them when merging). */
export function senderQueryParams(query: SenderQuery): Params {
  const d = DEFAULT_SENDER_QUERY;
  return {
    search: query.search || null,
    page: query.page === d.page ? null : query.page,
    pageSize: query.pageSize === d.pageSize ? null : query.pageSize,
    sort: query.sort === d.sort ? null : query.sort,
    dir: query.dir === d.dir ? null : query.dir,
  };
}

const HOSTNAME =
  /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
const LOCAL_PART = /^[a-z0-9!#$%&'*+/=?^_`|~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`|~-]+)*$/;
const MAX_ADDRESS = 254;
const MAX_DOMAIN = 253;
const MAX_LOCAL = 64;

/**
 * Mirrors the API's sender fetch target check: trimmed and lower-cased, an address `local@domain` or a
 * domain of at least two labels, optionally written `@domain`. Returns the normalised target, or `null`.
 */
export function normaliseFetchTarget(input: string | null | undefined): string | null {
  let target = (input ?? '').trim().toLowerCase();
  if (!target) return null;
  if (target.startsWith('@') && !target.includes('@', 1)) target = target.slice(1);
  const at = target.indexOf('@');
  if (at < 0) return isHostname(target) ? target : null;
  const local = target.slice(0, at);
  const ok =
    target.length <= MAX_ADDRESS &&
    at <= MAX_LOCAL &&
    LOCAL_PART.test(local) &&
    isHostname(target.slice(at + 1));
  return ok ? target : null;
}

function isHostname(value: string): boolean {
  return value.length <= MAX_DOMAIN && HOSTNAME.test(value);
}

/** Analysed share of a sender's mail, 0–100. */
export function analysedPercent(sender: Pick<SenderDto, 'analysedCount' | 'totalCount'>): number {
  if (sender.totalCount <= 0) return 0;
  return Math.min(100, Math.round((sender.analysedCount / sender.totalCount) * 100));
}

const UNITS: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
];

/** "3 days ago", "in 2 hours", "just now"; in the browser's locale. */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000);
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  for (const [unit, size] of UNITS) {
    if (Math.abs(seconds) >= size) return format.format(Math.trunc(seconds / size), unit);
  }
  return format.format(0, 'second');
}
