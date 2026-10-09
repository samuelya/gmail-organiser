import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ClaudeApiModels } from '../settings/llm-provider.models';
import { QUIET_STATUSES } from './error.interceptor';

/** `OllamaModelDto`: one model installed on the Ollama server. */
export interface OllamaModel {
  name: string;
  sizeBytes: number;
  family: string | null;
  parameterSize: string | null;
  capabilities: string[];
}

/** `LlmModelsDto`: models split by capability; an unreachable server is `reachable: false` with `error`. */
export interface LlmModels {
  reachable: boolean;
  version: string | null;
  chatModels: OllamaModel[];
  embeddingModels: OllamaModel[];
  error: string | null;
}

export type ModelKind = 'chat' | 'embedding';

/** `TestModelResultDto`: `elapsedMs` includes the time Ollama needs to load the model. */
export interface TestModelResult {
  ok: boolean;
  elapsedMs: number;
  error: string | null;
}

/** The Ollama endpoints (`/api/llm`) and the Claude API key, models and model test. */
@Injectable({ providedIn: 'root' })
export class LlmService {
  private readonly http = inject(HttpClient);

  /** Lists the models; `baseUrl` tests an unsaved URL, otherwise the saved one is used. */
  getModels(baseUrl?: string | null): Observable<LlmModels> {
    const params = baseUrl ? new HttpParams().set('baseUrl', baseUrl) : undefined;
    return this.http.get<LlmModels>('/api/llm/models', { params });
  }

  /**
   * Loads the model and runs a tiny prompt (or embedding). Can take minutes on a large model:
   * no client timeout; unsubscribing aborts the request.
   */
  testModel(kind: ModelKind, model: string, baseUrl?: string | null): Observable<TestModelResult> {
    return this.http.post<TestModelResult>('/api/llm/test-model', {
      kind,
      model,
      baseUrl: baseUrl || null,
    });
  }

  /**
   * Saves the Claude API key (write-only: no read returns it); a bad key is a 400 on `apiKey`, shown
   * under the input rather than by the error interceptor.
   */
  setClaudeApiKey(apiKey: string): Observable<void> {
    return this.http.put<void>(
      '/api/llm/claude-api/key',
      { apiKey },
      { context: new HttpContext().set(QUIET_STATUSES, [400]) },
    );
  }

  clearClaudeApiKey(): Observable<void> {
    return this.http.delete<void>('/api/llm/claude-api/key');
  }

  /** The account's Claude models, newest first; a failed listing is `reachable: false` with `error`. */
  getClaudeApiModels(): Observable<ClaudeApiModels> {
    return this.http.get<ClaudeApiModels>('/api/llm/claude-api/models');
  }

  /**
   * Runs a tiny prompt on a Claude model; unsubscribing aborts the request. No saved key is a 409,
   * shown next to the Test button rather than by the error interceptor.
   */
  testClaudeApiModel(model: string): Observable<TestModelResult> {
    return this.http.post<TestModelResult>(
      '/api/llm/claude-api/test-model',
      { model },
      { context: new HttpContext().set(QUIET_STATUSES, [409]) },
    );
  }
}

/** "4.7 GB" style size for model lists. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${value >= 10 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

/** Dropdown label: name, size and parameter size. */
export function modelLabel(model: OllamaModel): string {
  return [model.name, formatBytes(model.sizeBytes), model.parameterSize]
    .filter((part) => !!part)
    .join(' · ');
}
