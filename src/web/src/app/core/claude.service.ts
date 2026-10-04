import { Clipboard } from '@angular/cdk/clipboard';
import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import {
  ClaudeTestResult,
  CreateExternalReviewsRequest,
  CreateExternalReviewsResponse,
  ExternalReviewDto,
  McpConfig,
} from './claude.models';
import { PagedDto } from './paging.models';

/** Why copying the review prompt failed; a failed request is already shown by the error interceptor. */
export type CopyPromptResult = 'copied' | 'load_failed' | 'copy_failed';

/** Targets that narrow the review item list. */
export interface ReviewListTargets {
  labelPlanId?: string;
  findingIds?: readonly string[];
}

/** The Claude review endpoints (`/api/claude`), shared by Settings, Review and Rules. */
@Injectable({ providedIn: 'root' })
export class ClaudeService {
  private readonly http = inject(HttpClient);
  private readonly clipboard = inject(Clipboard);

  /** The MCP endpoint and the Claude Desktop snippet; creates the MCP token on first use. */
  getMcpConfig(): Observable<McpConfig> {
    return this.http.get<McpConfig>('/api/claude/mcp-config');
  }

  /** Replaces the MCP token; clients with the old snippet stop working. */
  rotateMcpToken(): Observable<McpConfig> {
    return this.http.post<McpConfig>('/api/claude/mcp-token/rotate', null);
  }

  /** The `review-pending` prompt as plain text, for pasting into Claude Desktop. */
  getReviewPrompt(): Observable<string> {
    return this.http.get('/api/claude/prompts/review-pending', { responseType: 'text' });
  }

  /** Tests the saved mode; never fails for a Claude problem, which comes back as `error`. */
  testConnection(): Observable<ClaudeTestResult> {
    return this.http.post<ClaudeTestResult>('/api/claude/test', null);
  }

  /** Queues one item per target without an open item; the others count as skipped. */
  createReviews(request: CreateExternalReviewsRequest): Observable<CreateExternalReviewsResponse> {
    return this.http.post<CreateExternalReviewsResponse>('/api/claude/reviews', request);
  }

  /**
   * Review items, newest first. `labelPlanId` and `findingIds` (at most 100, any of them) narrow the list to those
   * targets; both given means items matching both.
   */
  list(
    page = 1,
    pageSize = 100,
    targets: ReviewListTargets = {},
  ): Observable<PagedDto<ExternalReviewDto>> {
    const params: Record<string, string | number | readonly string[]> = { page, pageSize };
    if (targets.labelPlanId) params['labelPlanId'] = targets.labelPlanId;
    if (targets.findingIds?.length) params['findingId'] = targets.findingIds;
    return this.http.get<PagedDto<ExternalReviewDto>>('/api/claude/reviews', { params });
  }

  /** Queued items only. */
  cancel(id: string): Observable<ExternalReviewDto> {
    return this.action(id, 'cancel');
  }

  /** Approves Claude's outcome for the item's pending suggestions; 409 for `needs_human`. */
  accept(id: string): Observable<ExternalReviewDto> {
    return this.action(id, 'accept');
  }

  /** Keeps the local suggestion; nothing else changes. */
  dismiss(id: string): Observable<ExternalReviewDto> {
    return this.action(id, 'dismiss');
  }

  /** Queues an unavailable item again. */
  retry(id: string): Observable<ExternalReviewDto> {
    return this.action(id, 'retry');
  }

  /**
   * Copies the review prompt. Starts the clipboard write inside the click, with the prompt still
   * loading, so the browser keeps the user gesture; copying after the response fails in Safari and Firefox.
   */
  copyReviewPrompt(): Promise<CopyPromptResult> {
    const prompt = firstValueFrom(this.getReviewPrompt());
    // The write can reject before the prompt loads, so a failure waits for the request to settle.
    let loadFailed = false;
    const loadSettled = prompt.then(
      () => undefined,
      () => {
        loadFailed = true;
      },
    );
    let write: Promise<void>;
    if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
      const blob = prompt.then((text) => new Blob([text], { type: 'text/plain' }));
      // A refused write may never read the blob; loadSettled reports a failed load instead.
      blob.catch(() => undefined);
      write = navigator.clipboard.write([new ClipboardItem({ 'text/plain': blob })]);
    } else {
      write = prompt.then((text) => {
        if (!this.clipboard.copy(text)) throw new Error('copy failed');
      });
    }
    return write.then(
      () => 'copied',
      () => loadSettled.then((): CopyPromptResult => (loadFailed ? 'load_failed' : 'copy_failed')),
    );
  }

  private action(id: string, name: string): Observable<ExternalReviewDto> {
    return this.http.post<ExternalReviewDto>(
      `/api/claude/reviews/${encodeURIComponent(id)}/${name}`,
      null,
    );
  }
}
