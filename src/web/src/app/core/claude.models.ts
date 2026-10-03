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
