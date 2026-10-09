/** `LlmProvider` as the API serialises it: who answers the analysis prompts. */
export type LlmProvider = 'ollama' | 'claude_api';

/** The provider fields of `SettingsDto` (`GET /api/settings`); the key itself is never returned. */
export interface LlmProviderSettings {
  llmProvider: LlmProvider;
  /** The Claude API model; `null` when none is chosen. */
  claudeApiModel: string | null;
  claudeApiKeySet: boolean;
  /** The last characters of the saved key (`…abcd`); `null` when no key is saved. */
  claudeApiKeyHint: string | null;
}

/** A partial `PUT /api/settings`: fields left out stay unchanged. */
export interface LlmProviderUpdate {
  llmProvider?: LlmProvider;
  claudeApiModel?: string;
}

/** `ClaudeApiModelDto`: one model of the account, as the Claude API lists it. */
export interface ClaudeApiModel {
  id: string;
  displayName: string;
  createdAt: string | null;
}

/**
 * `ClaudeApiModelsDto` (`GET /api/llm/claude-api/models`), newest first. `error` can be set while
 * `reachable` is true (e.g. a capped list), so it is shown whenever present.
 */
export interface ClaudeApiModels {
  keySet: boolean;
  reachable: boolean;
  error: string | null;
  models: ClaudeApiModel[];
}
