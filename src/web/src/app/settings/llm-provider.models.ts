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
