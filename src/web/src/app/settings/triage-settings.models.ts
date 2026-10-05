import { HttpErrorResponse } from '@angular/common/http';
import { AbstractControl, ValidationErrors } from '@angular/forms';
import type { Range } from './settings.models';

/** `RetentionSettingsDto` (`retention` in `GET /api/settings`, #368): days per snake_case mail type, `null` = keep. */
export interface RetentionSettings {
  enabled: boolean;
  days: Record<string, number | null>;
}

/** The retention field of `SettingsDto`. */
export interface RetentionSettingsDto {
  retention: RetentionSettings;
}

/** The retention part of `UpdateSettingsRequest`: only the types listed change; `null` = keep. */
export interface RetentionUpdate {
  retention: { enabled?: boolean; days?: Record<string, number | null> };
}

/** `GET /api/clean-up/retention`; `eligibleNow` is an upper bound (protected and kept mail is skipped at run time). */
export interface RetentionStatusDto {
  enabled: boolean;
  lastRunAt: string | null;
  lastMarked: number | null;
  nextDueAt: string | null;
  eligibleNow: number;
}

/** The job `POST /api/clean-up/retention/run` queues (`RetentionSweepJob.JobType`). */
export const RETENTION_JOB_TYPE = 'retention_sweep';

/** The API's `RetentionSettings.MinDays` / `MaxDays`. */
export const RETENTION_DAYS: Range = { min: 1, max: 3650, step: 1, integer: true };

/** The taxonomy fields of `SettingsDto` (#367). */
export interface TaxonomySettings {
  /** Topic labels come from the label tree only; a new one is a proposal approved one by one. */
  taxonomyLocked: boolean;
  analysisMaxNewLabelsPerRun: number;
  /** Names the analysis never uses, at any level of a label path. */
  analysisBlockedLabels: string[];
}

export type TaxonomyUpdate = Partial<TaxonomySettings>;

/** The API's `SettingsValidation` limits for the taxonomy fields. */
export const MAX_NEW_LABELS_PER_RUN: Range = { min: 0, max: 50, step: 1, integer: true };
export const MAX_BLOCKED_LABELS = 50;
export const MAX_BLOCKED_LABEL_LENGTH = 100;

/** Why the API would refuse `name` as a blocked label, or null when it is valid. */
export function blockedLabelError(name: string): string | null {
  const trimmed = name.trim();
  if (!trimmed) return 'Enter a label name.';
  if (trimmed.length > MAX_BLOCKED_LABEL_LENGTH)
    return `At most ${MAX_BLOCKED_LABEL_LENGTH} characters.`;
  if (trimmed.includes('/')) return "One level of a label, without '/'.";
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f-\u009f]/.test(trimmed)) return 'No control characters.';
  return null;
}

/** The triage fields of `SettingsDto` (#375); `triageModel: null` is off. */
export interface TriageModelSettings {
  triageModel: string | null;
  triageConfidenceThreshold: number;
}

/** The pack fields of `SettingsDto` (#376); a pack size of 1 is off. */
export interface PackSettings {
  analysisPackSize: number;
  analysisPackRetryThreshold: number;
}

/** The triage and pack part of `UpdateSettingsRequest`; `triageModel: ''` clears the model. */
export type TriageUpdate = Partial<
  { triageModel: string; triageConfidenceThreshold: number } & PackSettings
>;

/** The API's `SettingsValidation` limits for the triage and pack fields. */
export const TRIAGE_CONFIDENCE_THRESHOLD: Range = { min: 0, max: 1, step: 0.01, integer: false };
export const PACK_SIZE: Range = { min: 1, max: 30, step: 1, integer: true };
export const PACK_RETRY_THRESHOLD: Range = { min: 0, max: 1, step: 0.01, integer: false };

/** "70 %" for a 0–1 threshold. */
export function percent(value: number | null): string {
  return value === null ? '' : `${Math.round(value * 100)} %`;
}

/** `min`/`max` accept 2.5; the API's integer fields would reject it. */
export function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  return control.value === null || Number.isInteger(control.value) ? null : { integer: true };
}

/** The field errors of a 400 ValidationProblem, keyed as the API names them; empty for anything else. */
export function problemErrors(error: unknown): Record<string, string[]> {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return {};
  const body = error.error as { title?: string; errors?: Record<string, string[]> } | null;
  return body?.errors ?? (body?.title ? { '': [body.title] } : {});
}
