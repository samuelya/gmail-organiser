import { formatNumber } from '@angular/common';
import { ChartLinkCell, ChartTable } from '../core/charts/chart.models';
import { mailTypeLabel } from '../review/mail-type-chip.component';
import { MAIL_TYPE_PARAM, mailTypesParam } from '../review/mail-type-filter';

/** Live messages per Gmail category tab. */
export interface CategoryCountsDto {
  primary: number;
  promotions: number;
  social: number;
  updates: number;
  forums: number;
}

/** Suggestions (pending, approved or applied; not rejected) with one snake_case mail type. */
export interface MailTypeCountDto {
  mailType: string;
  count: number;
}

/** `GET /api/dashboard/breakdown`: every mail type in enum order, zero included. */
export interface MailboxBreakdownDto {
  categories: CategoryCountsDto;
  mailTypes: MailTypeCountDto[];
  unclassified: number;
}

/** Gmail's category tabs in the order Gmail shows them. */
export const GMAIL_CATEGORIES: readonly { key: keyof CategoryCountsDto; label: string }[] = [
  { key: 'primary', label: 'Primary' },
  { key: 'promotions', label: 'Promotions' },
  { key: 'social', label: 'Social' },
  { key: 'updates', label: 'Updates' },
  { key: 'forums', label: 'Forums' },
];

export const UNCLASSIFIED_LABEL = 'Unclassified';

/** One doughnut slice; `percent` is the share of all categorised messages (0–100). */
export interface CategorySlice {
  label: string;
  count: number;
  percent: number;
}

/** One bar; `link` is where clicking it goes, null for "Unclassified" or a type the review page can't filter. */
export interface MailTypeBar {
  label: string;
  count: number;
  /** False for the "Unclassified" bar. */
  classified: boolean;
  link: ChartLinkCell | null;
}

/** All five tabs in Gmail's order; none when no message has a category. */
export function categorySlices(categories: CategoryCountsDto): CategorySlice[] {
  const total = GMAIL_CATEGORIES.reduce((sum, c) => sum + categories[c.key], 0);
  if (total === 0) return [];
  return GMAIL_CATEGORIES.map((c) => ({
    label: c.label,
    count: categories[c.key],
    percent: (categories[c.key] / total) * 100,
  }));
}

/** Types with suggestions, most first (ties by label), then "Unclassified" when there are any. */
export function mailTypeBars(dto: MailboxBreakdownDto): MailTypeBar[] {
  const bars = dto.mailTypes
    .filter((t) => t.count > 0)
    .map((t) => {
      const label = mailTypeLabel(t.mailType);
      return { label, count: t.count, classified: true, link: reviewLink(t.mailType, label) };
    })
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
  if (dto.unclassified > 0) {
    bars.push({
      label: UNCLASSIFIED_LABEL,
      count: dto.unclassified,
      classified: false,
      link: null,
    });
  }
  return bars;
}

/** The review page filtered to one type on "All but rejected": the bar counts every status but rejected. */
function reviewLink(mailType: string, text: string): ChartLinkCell | null {
  const param = mailTypesParam([mailType]);
  return param
    ? { text, link: '/review', queryParams: { status: 'not_rejected', [MAIL_TYPE_PARAM]: param } }
    : null;
}

/** The largest tab and its share: the doughnut's text alternative. */
export function categorySummary(slices: readonly CategorySlice[]): string {
  const top = slices.reduce<CategorySlice | null>(
    (a, b) => (!a || b.count > a.count ? b : a),
    null,
  );
  if (!top) return 'No mail fetched yet.';
  return `Most of your mail is in ${top.label} (${Math.round(top.percent)}%).`;
}

/** The commonest type out of all classified suggestions: the bar chart's text alternative. */
export function mailTypeSummary(bars: readonly MailTypeBar[], locale: string): string {
  const count = (n: number) => formatNumber(n, locale, '1.0-0');
  const typed = bars.filter((b) => b.classified);
  const top = typed[0];
  if (!top) {
    const unclassified = bars.reduce((sum, b) => sum + b.count, 0);
    return unclassified
      ? `None of the ${count(unclassified)} suggestions has a mail type yet.`
      : 'No suggestions yet.';
  }
  const classified = typed.reduce((sum, b) => sum + b.count, 0);
  return `${capitalise(top.label)} is the most common mail type (${count(top.count)} of ${count(classified)} classified, not counting rejected).`;
}

export function categoryTable(slices: readonly CategorySlice[]): ChartTable {
  return {
    columns: ['Category', 'Messages', 'Share %'],
    rows: slices.map((s) => [s.label, s.count, Math.round(s.percent * 10) / 10]),
  };
}

/** The bars as rows; a clickable bar's label links where the bar goes. */
export function mailTypeTable(bars: readonly MailTypeBar[]): ChartTable {
  return {
    columns: ['Mail type', 'Suggestions (not rejected)'],
    rows: bars.map((b) => [b.link ?? b.label, b.count]),
  };
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
