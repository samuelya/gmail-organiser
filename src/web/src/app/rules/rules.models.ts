import { SenderPatternDto } from '../review/review.models';

/** `FilterCriteriaDto`: Gmail's filter criteria; null fields are unset. */
export interface FilterCriteria {
  from: string | null;
  to: string | null;
  subject: string | null;
  query: string | null;
  negatedQuery: string | null;
  hasAttachment: boolean | null;
  excludeChats: boolean | null;
  size: number | null;
  /** `smaller` or `larger`. */
  sizeComparison: string | null;
}

/** `LabelRefDto`: `name` is null when the mailbox no longer has the label. */
export interface LabelRef {
  id: string;
  name: string | null;
}

/** `FilterActionDto`: what an existing filter does. */
export interface FilterActionDto {
  addLabels: LabelRef[];
  removeLabelIds: string[];
  skipInbox: boolean;
  markRead: boolean;
  forwards: boolean;
}

/** `FilterActionRequest`: label paths (missing ones are created on create), skip inbox, mark read. */
export interface FilterActionRequest {
  addLabelNames: string[];
  skipInbox: boolean;
  markRead: boolean;
}

/** The body of `POST /api/rules/filters` and `/filters/preview`. */
export interface FilterRequest {
  criteria: FilterCriteria;
  action: FilterActionRequest;
}

export interface FilterDto {
  id: string;
  criteria: FilterCriteria;
  criteriaSummary: string;
  action: FilterActionDto;
  createdByApp: boolean;
  firstSeenAt: string;
  deletedAt: string | null;
  deletedByApp: boolean;
  restoredFrom: string | null;
}

/** `GET /api/rules/filters`: the snapshot; `syncedAt` is null before the first sync. */
export interface FilterListDto {
  syncedAt: string | null;
  activeCount: number;
  /** Gmail's per-account filter limit. */
  limit: number;
  filters: FilterDto[];
}

export interface FilterSyncResultDto {
  total: number;
  added: number;
  removed: number;
  syncedAt: string;
}

/** `POST /api/rules/filters/preview`. */
export interface FilterPreviewDto {
  criteria: FilterCriteria;
  criteriaSummary: string;
  /** The Gmail search equivalent of the criteria. */
  query: string;
  /** Stored messages matching; null when a criterion has no local equivalent (a warning says why). */
  localMatches: number | null;
  /** Gmail's estimate; null when Gmail could not answer (a warning says why). */
  gmailEstimate: number | null;
  action: FilterActionDto;
  /** Label paths a create would add to the mailbox. */
  createsLabels: string[];
  warnings: string[];
}

/** `FilterProposalDto`: an approved sender no active filter covers, with the suggested filter. */
export interface FilterProposalDto {
  senderAddress: string;
  displayName: string | null;
  messageCount: number;
  listId: string | null;
  pattern: SenderPatternDto;
  suggested: FilterRequest;
}

/** The preview dialog's form value: one label path plus the optional archive-rule label. */
export interface FilterEdit {
  from: string;
  to: string;
  subject: string;
  hasAttachment: boolean;
  query: string;
  negatedQuery: string;
  labelPath: string;
  /** A label of `settings.appsScript.rules`, or '' for none. */
  archiveLabel: string;
  skipInbox: boolean;
  markRead: boolean;
}

export const PROPOSALS_PAGE_SIZE = 20;

/** The `?tab=` values of `/rules`, in tab order. */
export const RULES_TABS = ['filters', 'findings', 'labels'] as const;
export type RulesTab = (typeof RULES_TABS)[number];

/** The action chips of a filter row. */
export function actionChips(action: FilterActionDto): string[] {
  return [
    ...action.addLabels.map((l) => l.name ?? '(deleted label)'),
    ...(action.skipInbox ? ['skip inbox'] : []),
    ...(action.markRead ? ['mark read'] : []),
    ...(action.forwards ? ['forwards'] : []),
  ];
}
