import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ClaudeTestResult, McpConfig } from './claude.models';

/** The Claude review endpoints (`/api/claude`), shared by Settings and Review. */
@Injectable({ providedIn: 'root' })
export class ClaudeService {
  private readonly http = inject(HttpClient);

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
}
