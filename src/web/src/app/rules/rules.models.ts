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

/**
 * `FilterProposalDto`: an approved policy, policy rule or sender no active filter covers, with the
 * suggested filter. `key` is unique within a listing; `senderAddress` isn't for policy proposals.
 */
export interface FilterProposalDto {
  key: string;
  source?: 'policy' | 'pattern';
  policyId?: string | null;
  ruleId?: string | null;
  /** A rule condition has no Gmail equivalent, so the filter matches more than the rule. */
  partial?: boolean;
  note?: string | null;
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

export type FilterFindingKind =
  'duplicate' | 'overlap' | 'deleted_label' | 'no_recent_matches' | 'mergeable';
/** `none` is report only: there is no safe fix, so there is nothing to apply. */
export type FilterFixKind = 'none' | 'delete' | 'merge' | 'merge_actions' | 'drop_label';
export type FilterFindingStatus = 'open' | 'applied' | 'dismissed' | 'superseded';

/** `FilterFixDto`: apply creates `create` (if any) first, then deletes `deleteFilterIds`. */
export interface FilterFixDto {
  kind: FilterFixKind;
  deleteFilterIds: string[];
  create: { criteria: FilterCriteria; action: FilterActionDto } | null;
}

export interface FilterFindingDto {
  id: string;
  kind: FilterFindingKind;
  filterIds: string[];
  /** The rows of `filterIds`, deleted ones included. */
  filters: FilterDto[];
  description: string;
  fix: FilterFixDto;
  status: FilterFindingStatus;
  appliedAt: string | null;
  /** Why the last apply stopped; the finding stays open. */
  error: string | null;
  /** The review that found it; another review's id marks a half-applied finding carried over. */
  reviewId: string;
}

/** `FilterReviewDto`: one run of the filter checks, with the optional local-model summary. */
export interface FilterReviewDto {
  id: string;
  createdAt: string;
  /** Active filters the review checked. */
  filterCount: number;
  findings: FilterFindingDto[];
  summary: string | null;
  summaryModel: string | null;
  summarisedAt: string | null;
  summaryError: string | null;
}

/** The finding kinds in display order, with their group headings. */
export const FINDING_KINDS: readonly { kind: FilterFindingKind; label: string }[] = [
  { kind: 'duplicate', label: 'Duplicates' },
  { kind: 'overlap', label: 'Overlaps' },
  { kind: 'deleted_label', label: 'Deleted labels' },
  { kind: 'no_recent_matches', label: 'No recent matches' },
  { kind: 'mergeable', label: 'Mergeable' },
];

export interface FindingGroup {
  kind: FilterFindingKind;
  label: string;
  findings: FilterFindingDto[];
}

/** Open findings grouped by kind in `FINDING_KINDS` order (empty groups left out), and the resolved ones. */
export function groupFindings(findings: readonly FilterFindingDto[]): {
  groups: FindingGroup[];
  resolved: FilterFindingDto[];
} {
  const open = findings.filter((f) => f.status === 'open');
  return {
    groups: FINDING_KINDS.map(({ kind, label }) => ({
      kind,
      label,
      findings: open.filter((f) => f.kind === kind),
    })).filter((g) => g.findings.length > 0),
    resolved: findings.filter((f) => f.status !== 'open'),
  };
}

/** Text of a filter's criteria, in the API's `criteriaSummary` format. */
export function criteriaText(c: FilterCriteria): string {
  const parts: string[] = [];
  if (c.from) parts.push(`from:${c.from}`);
  if (c.to) parts.push(`to:${c.to}`);
  if (c.subject) parts.push(`subject:"${c.subject}"`);
  if (c.hasAttachment === true) parts.push('has:attachment');
  if (c.size != null) parts.push(`${c.sizeComparison ?? 'size'}:${c.size}`);
  if (c.negatedQuery) parts.push(`-(${c.negatedQuery})`);
  if (c.query) parts.push(`(${c.query})`);
  return parts.join(' ');
}

/** A proposed fix, ready to render: what it creates and which filters it deletes. */
export interface FixView {
  title: string;
  create: { criteria: string; chips: string[] } | null;
  /** Criteria summaries of the filters the fix deletes (the id when the row is unknown). */
  deletes: string[];
}

const FIX_TITLES: Record<Exclude<FilterFixKind, 'none'>, string> = {
  delete: 'Delete the redundant filter',
  merge: 'Merge into one filter',
  merge_actions: 'Combine the actions into one filter',
  drop_label: 'Re-create without the deleted label',
};

/** The fix of a finding for display; null for `none` (report only). */
export function fixView(finding: FilterFindingDto): FixView | null {
  const fix = finding.fix;
  if (fix.kind === 'none') return null;
  const summary = (id: string) => finding.filters.find((f) => f.id === id)?.criteriaSummary ?? id;
  return {
    title: FIX_TITLES[fix.kind],
    create: fix.create
      ? { criteria: criteriaText(fix.create.criteria), chips: actionChips(fix.create.action) }
      : null,
    deletes: fix.deleteFilterIds.map(summary),
  };
}
