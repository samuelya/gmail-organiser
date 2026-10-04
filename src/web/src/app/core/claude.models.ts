/**
 * `McpConfigDto` (`GET /api/claude/mcp-config`). The DTO also carries the bare MCP token; the web
 * reads only the snippet, which contains it by design (DESIGN §6.7).
 */
export interface McpConfig {
  endpointUrl: string;
  /** The `claude_desktop_config.json` entry. */
  claudeDesktopSnippet: string;
}

/** `ClaudeTestResultDto` (`POST /api/claude/test`); `error` is shown verbatim. */
export interface ClaudeTestResult {
  ok: boolean;
  mode: string;
  cliVersion: string | null;
  tokenSet: boolean;
  mcpReachable: boolean;
  elapsedMs: number;
  error: string | null;
}

/** `ExternalReviewStatus` as the API writes it. */
export type ExternalReviewStatus = 'queued' | 'running' | 'reviewed' | 'unavailable' | 'cancelled';
/** `ReviewVerdict`: Claude agrees, suggests another outcome, or leaves it to the user. */
export type ReviewVerdict = 'agree' | 'alternative' | 'needs_human';
/** What the user did with a reviewed item. */
export type ExternalReviewResolution = 'none' | 'accepted_claude' | 'dismissed';

/** What a Claude review item is about. */
export type ExternalReviewTarget = 'suggestion' | 'group' | 'label_plan' | 'filter_finding';

/** `ExternalReviewDto`: one Claude review item for a suggestion, a review group, a label plan or a filter finding. */
export interface ExternalReviewDto {
  id: string;
  targetType: ExternalReviewTarget;
  suggestionId: string | null;
  senderAddress: string;
  groupKey: string | null;
  groupDisplay: string | null;
  status: ExternalReviewStatus;
  reviewer: string | null;
  verdict: ReviewVerdict | null;
  verdictTopicLabel: string | null;
  verdictNeedsAction: boolean | null;
  verdictToBeDeleted: boolean | null;
  /** The alternative's document type; null or `""` with `verdictDocumentTypeSet` clears it. */
  verdictDocumentTypeLabel: string | null;
  /** False: the verdict leaves each member's document type unchanged. */
  verdictDocumentTypeSet: boolean;
  reasoning: string | null;
  /** Shown verbatim. */
  error: string | null;
  resolution: ExternalReviewResolution;
  createdAt: string;
  reviewedAt: string | null;
  resolvedAt: string | null;
  labelPlanId?: string | null;
  findingId?: string | null;
  /** A label plan's alternative: the label paths Claude proposes. */
  alternativeStructure?: string[] | null;
  /** A finding's alternative: a Gmail query as given, or structured criteria as compact JSON. */
  verdictFilterCriteria?: string | null;
}

export interface GroupRef {
  senderAddress: string;
  groupKey: string;
}

/** `CreateExternalReviewsRequest`: at least one field. The plan must be a draft, the findings open. */
export interface CreateExternalReviewsRequest {
  suggestionIds?: string[];
  groups?: GroupRef[];
  runId?: string;
  labelPlanId?: string;
  findingIds?: string[];
}

export interface CreateExternalReviewsResponse {
  created: number;
  /** Targets that already had an open item. */
  skipped: number;
  items: ExternalReviewDto[];
}

/** The API's `ExternalReviewService.MaxTargets`: suggestions and groups per request. */
export const MAX_CLAUDE_TARGETS = 200;

/** Queued, running, or reviewed and not yet resolved: the API refuses a second item for the target. */
export function isOpenReview(item: ExternalReviewDto | null | undefined): boolean {
  if (!item) return false;
  return (
    item.status === 'queued' ||
    item.status === 'running' ||
    (item.status === 'reviewed' && item.resolution === 'none')
  );
}

/** The Rules page's key for a plan or finding item; null for the Review page's targets. */
export function ruleTargetKey(item: ExternalReviewDto): string | null {
  if (item.targetType === 'label_plan' && item.labelPlanId) return `plan:${item.labelPlanId}`;
  if (item.targetType === 'filter_finding' && item.findingId) return `finding:${item.findingId}`;
  return null;
}

/** The snackbar after "Send to Claude". */
export function sentMessage(r: Pick<CreateExternalReviewsResponse, 'created' | 'skipped'>): string {
  const created = `Sent ${r.created} ${r.created === 1 ? 'item' : 'items'} to Claude`;
  return r.skipped ? `${created}; ${r.skipped} already open.` : `${created}.`;
}
