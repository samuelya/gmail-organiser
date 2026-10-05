import { AbstractControl, ValidationErrors } from '@angular/forms';
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
  /** The address itself is allowlisted. */
  allowlisted: boolean;
  /** The sender's domain, or a parent domain, is in `protection.allowlistedDomains`. */
  allowlistedByDomain: boolean;
  /** The queued, running or paused `sender_fetch` job targeting this address or its domain. */
  activeFetchJob: JobDto | null;
  /** When the sender was last unsubscribed from (one-click, or a link / `mailto:` the user marked). */
  unsubscribedAt: string | null;
  /** The relay-decoded address senders are grouped by; `address` when it is not a relay. */
  canonicalAddress: string;
  canonicalDomain: string;
  /** `address` is a relay (e.g. a mailing service) that `canonicalAddress` was decoded from. */
  isRelay: boolean;
  kind: SenderKind;
  unreadCount: number;
  repliedCount: number;
  firstSeenAt: string | null;
}

/** How a sender writes, from its Stage-0 stats. */
export type SenderKind = 'human' | 'bulk' | 'mixed' | 'unknown';

/** Every kind, in the order the filter shows them. */
export const SENDER_KINDS: readonly SenderKind[] = ['human', 'bulk', 'mixed', 'unknown'];

/** `POST /api/fetch/sender`: `created` for a new job (202), not when the target's job was active or resumed (200). */
export interface SenderFetchStarted {
  jobId: string;
  created: boolean;
}

export type SenderSort = 'total' | 'lastSeen' | 'address' | 'analysed' | 'unread';

/** The senders list request; also the page's URL query params. */
export interface SenderQuery {
  search: string;
  page: number;
  pageSize: number;
  sort: SenderSort;
  dir: SortDirection;
  /** Only senders of these kinds; empty for all. The URL repeats `kind`. */
  kinds: readonly SenderKind[];
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
  kinds: [],
};

/** What the API's `char.IsControl` rejects: C0, DEL and C1. */
const CONTROL_CHARS = /\p{Cc}/gu;

export function hasControlChars(value: string): boolean {
  return value.search(CONTROL_CHARS) >= 0;
}

/** Search text as the API accepts it: control characters (a pasted tab or newline) become spaces. */
export function cleanSearch(value: string): string {
  return value.replace(CONTROL_CHARS, ' ').trim().slice(0, MAX_SEARCH_LENGTH).trim();
}

const SORTS: readonly SenderSort[] = ['total', 'lastSeen', 'address', 'analysed', 'unread'];
/** Keeps a hand-edited URL inside the API's page limit, so it never answers 400. */
const MAX_PAGE = 1_000_000;

/** Reads the query from URL params; anything missing or invalid falls back to the default. */
export function parseSenderQuery(params: ParamMap): SenderQuery {
  const d = DEFAULT_SENDER_QUERY;
  const page = Number(params.get('page'));
  const pageSize = Number(params.get('pageSize'));
  const sort = params.get('sort') as SenderSort | null;
  const dir = params.get('dir');
  const kinds = new Set(params.getAll('kind'));
  return {
    search: cleanSearch(params.get('search') ?? ''),
    page: Number.isInteger(page) && page >= 1 && page <= MAX_PAGE ? page : d.page,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : d.pageSize,
    sort: sort && SORTS.includes(sort) ? sort : d.sort,
    dir: dir === 'asc' || dir === 'desc' ? dir : d.dir,
    kinds: SENDER_KINDS.filter((kind) => kinds.has(kind)),
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
    kind: query.kinds.length ? [...query.kinds] : null,
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

/**
 * An allowlist address as the API stores it: one surrounding `<…>` stripped, trimmed and lower-cased,
 * a plain `local@domain`. Returns the normalised address, or `null` (a bare domain is not an address).
 */
export function normaliseAllowlistAddress(input: string | null | undefined): string | null {
  let value = (input ?? '').trim();
  if (value.length >= 2 && value.startsWith('<') && value.endsWith('>')) value = value.slice(1, -1);
  if (value.trim().startsWith('@')) return null;
  const address = normaliseFetchTarget(value);
  return address?.includes('@') ? address : null;
}

function isHostname(value: string): boolean {
  return value.length <= MAX_DOMAIN && HOSTNAME.test(value);
}

/** API-side rules for a sender fetch target; blank (spaces only, too) is `required`. */
export function fetchTargetValidator(control: AbstractControl<string>): ValidationErrors | null {
  if (!control.value.trim()) return { required: true };
  return normaliseFetchTarget(control.value) ? null : { target: true };
}

/** The API rejects control characters in a search, e.g. a tab pasted from a spreadsheet. */
export function searchValidator(control: AbstractControl<string>): ValidationErrors | null {
  return hasControlChars(control.value) ? { controlChars: true } : null;
}

/** Last page that has rows; at least 1. */
export function lastPage(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / pageSize));
}

/** Analysed share of a sender's mail, 0–100. */
export function analysedPercent(sender: Pick<SenderDto, 'analysedCount' | 'totalCount'>): number {
  if (sender.totalCount <= 0) return 0;
  return Math.min(100, Math.round((sender.analysedCount / sender.totalCount) * 100));
}

/** Unread share of a sender's mail, 0–100. */
export function unreadPercent(sender: Pick<SenderDto, 'unreadCount' | 'totalCount'>): number {
  if (sender.totalCount <= 0) return 0;
  return Math.min(100, Math.round((sender.unreadCount / sender.totalCount) * 100));
}

/** A list row: one stable object per sender, with its analysed and unread shares. */
export function senderRow(sender: SenderDto) {
  return { sender, percent: analysedPercent(sender), unread: unreadPercent(sender) };
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

/** `SenderCategoryMixDto`: messages per Gmail category tab. */
export interface SenderCategoryMix {
  primary: number;
  promotions: number;
  social: number;
  updates: number;
  forums: number;
}

/** `NoisySenderDto` from `GET /api/senders/noisy`: one canonical sender, counts summed over its raw addresses. */
export interface NoisySenderDto {
  canonicalAddress: string;
  canonicalDomain: string;
  displayName: string | null;
  /** The raw addresses behind the canonical one, highest volume first. */
  addresses: string[];
  totalCount: number;
  unreadCount: number;
  /** 0–1. */
  unreadRatio: number;
  listUnsubscribeCount: number;
  kind: SenderKind;
  firstSeenAt: string | null;
  lastSeenAt: string | null;
  categoryMix: SenderCategoryMix;
  unsubscribedAt: string | null;
  /** An approved sender policy already handles this sender; it is not selectable. */
  hasApprovedPolicy: boolean;
}

/** The noisy-senders request; also the page's URL query params. */
export interface NoisyQuery {
  minMessages: number;
  /** 0–100; the API takes it as a 0–1 ratio. */
  minUnreadPercent: number;
  /** Only senders last seen more than this many days ago; `null` for all. */
  dormantDays: number | null;
  search: string;
  page: number;
  pageSize: number;
}

/** `POST /api/senders/noisy/proposals`: Stage-0 suggestions always mark To-Be-Deleted. */
export interface NoisyProposalRequest {
  canonicalAddresses: string[];
  toBeDeleted: true;
  unsubscribe: boolean;
}

/** `Stage0ProposalsResponse`. */
export interface NoisyProposalResult {
  created: number;
  skippedProtected: number;
  skippedAlreadySuggested: number;
}

/** `POST /api/senders/archive`. */
export interface ArchiveSendersRequest {
  canonicalAddresses: string[];
}

/** `sender_archive`, the job type `POST /api/senders/archive` enqueues. */
export const SENDER_ARCHIVE_JOB = 'sender_archive';

/** The API's limits for the noisy filters. */
export const MAX_MIN_MESSAGES = 100_000;
export const MAX_DORMANT_DAYS = 3650;

export const DEFAULT_NOISY_QUERY: Readonly<NoisyQuery> = {
  minMessages: 10,
  minUnreadPercent: 90,
  dormantDays: null,
  search: '',
  page: 1,
  pageSize: 25,
};

/** An integer param within `[min, max]`, or `null` when missing or out of range. */
function intParam(params: ParamMap, name: string, min: number, max: number): number | null {
  const raw = params.get(name);
  const value = raw === null || raw.trim() === '' ? NaN : Number(raw);
  return Number.isInteger(value) && value >= min && value <= max ? value : null;
}

/** Reads the noisy query from URL params; anything missing or invalid falls back to the default. */
export function parseNoisyQuery(params: ParamMap): NoisyQuery {
  const d = DEFAULT_NOISY_QUERY;
  const pageSize = Number(params.get('pageSize'));
  return {
    minMessages: intParam(params, 'minMessages', 1, MAX_MIN_MESSAGES) ?? d.minMessages,
    minUnreadPercent: intParam(params, 'minUnread', 0, 100) ?? d.minUnreadPercent,
    dormantDays: intParam(params, 'dormantDays', 1, MAX_DORMANT_DAYS),
    search: cleanSearch(params.get('search') ?? ''),
    page: intParam(params, 'page', 1, MAX_PAGE) ?? d.page,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : d.pageSize,
  };
}

/** URL query params for a noisy query; defaults are left out (`null` removes them when merging). */
export function noisyQueryParams(query: NoisyQuery): Params {
  const d = DEFAULT_NOISY_QUERY;
  return {
    minMessages: query.minMessages === d.minMessages ? null : query.minMessages,
    minUnread: query.minUnreadPercent === d.minUnreadPercent ? null : query.minUnreadPercent,
    dormantDays: query.dormantDays,
    search: query.search || null,
    page: query.page === d.page ? null : query.page,
    pageSize: query.pageSize === d.pageSize ? null : query.pageSize,
  };
}
