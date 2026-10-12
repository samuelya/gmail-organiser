import { ParamMap } from '@angular/router';
import { parseSenderParam } from '../analyse/analysis.models';

/** A suggestion's status. Applied is read-only. */
export type ReviewStatus = 'pending' | 'approved' | 'rejected' | 'applied';
/** The tabs `GET /api/review/senders` and `…/senders/{address}` filter by: one status, or every status but rejected. */
export type ReviewTab = ReviewStatus | 'not_rejected';
/** The query parameter that holds the tab: `/review?status=<tab>`. */
export const REVIEW_STATUS_PARAM = 'status';
/** "All but rejected": the statuses it lists (as `ReviewQuery.NotRejected` in the API) and how its texts name them. */
export const NOT_REJECTED_TAB = {
  value: 'not_rejected',
  label: 'All but rejected',
  statuses: ['pending', 'approved', 'applied'],
  phrase: 'other than rejected',
} as const satisfies {
  value: ReviewTab;
  label: string;
  statuses: readonly ReviewStatus[];
  phrase: string;
};
export const REVIEW_STATUSES: readonly { value: ReviewTab; label: string }[] = [
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
  { value: 'applied', label: 'Applied' },
  { value: NOT_REJECTED_TAB.value, label: NOT_REJECTED_TAB.label },
];

/** The tab names one status; "All but rejected" mixes them. */
export function isSingleStatus(tab: ReviewTab): tab is ReviewStatus {
  return tab !== NOT_REJECTED_TAB.value;
}

/** The page's tab and sender from `/review?status=<status>&sender=<address>`; anything invalid falls back. */
export function parseReviewParams(params: ParamMap): {
  status: ReviewTab;
  sender: string | null;
} {
  const status =
    REVIEW_STATUSES.find((s) => s.value === params.get(REVIEW_STATUS_PARAM))?.value ?? 'pending';
  return { status, sender: parseSenderParam(params) };
}
