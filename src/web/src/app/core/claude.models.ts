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

/** `ExternalReviewDto`: one Claude review item for a suggestion or a review group. */
export interface ExternalReviewDto {
  id: string;
  targetType: 'suggestion' | 'group';
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
  reasoning: string | null;
  /** Shown verbatim. */
  error: string | null;
  resolution: ExternalReviewResolution;
  createdAt: string;
  reviewedAt: string | null;
  resolvedAt: string | null;
}

export interface GroupRef {
  senderAddress: string;
  groupKey: string;
}

/** `CreateExternalReviewsRequest`: at least one of the three. */
export interface CreateExternalReviewsRequest {
  suggestionIds?: string[];
  groups?: GroupRef[];
  runId?: string;
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

/** The snackbar after "Send to Claude". */
export function sentMessage(r: Pick<CreateExternalReviewsResponse, 'created' | 'skipped'>): string {
  const created = `Sent ${r.created} ${r.created === 1 ? 'item' : 'items'} to Claude`;
  return r.skipped ? `${created}; ${r.skipped} already open.` : `${created}.`;
}
