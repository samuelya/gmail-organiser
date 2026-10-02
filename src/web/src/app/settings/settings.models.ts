import { AppSettings } from '../setup/setup.service';

/** `AnalysisGroupingMode` as the API serialises it. */
export type AnalysisGroupingMode = 'off' | 'sender_subject' | 'auto';

/** The analysis fields of `SettingsDto` (`GET /api/settings`). */
export interface AnalysisSettings {
  analysisDefaultCount: number;
  analysisBodyMaxChars: number;
  analysisGroupingMode: AnalysisGroupingMode;
  analysisRepresentativesPerGroup: number;
  analysisMinGroupSize: number;
  analysisDerivedConfidencePenalty: number;
  analysisClusterDistance: number;
  analysisMemoryShortCircuit: boolean;
  analysisMemoryMinApprovals: number;
  bulkApproveThreshold: number;
  autoArchiveOnActionDone: boolean;
  /** The custom prompt; `null` when the built-in one applies. */
  analysisPromptTemplate: string | null;
}

/** The full `SettingsDto`: the setup fields plus the analysis fields. */
export type SettingsDto = AppSettings & AnalysisSettings;

/**
 * The analysis part of `UpdateSettingsRequest`: only changed fields are sent; an empty prompt
 * template clears the custom prompt.
 */
export type AnalysisSettingsUpdate = Partial<
  Omit<AnalysisSettings, 'analysisPromptTemplate'> & { analysisPromptTemplate: string }
>;

/** `PromptTemplateDto` from `GET /api/analysis/prompt/default`. */
export interface PromptTemplateDto {
  version: string;
  template: string;
}

export type NumericAnalysisField = {
  [K in keyof AnalysisSettings]: AnalysisSettings[K] extends number ? K : never;
}[keyof AnalysisSettings];

export interface Range {
  min: number;
  max: number;
  step: number;
  integer: boolean;
}

/** Same bounds as the API's `SettingsValidation`; `step` only sets the arrow-key increment. */
export const ANALYSIS_LIMITS: Record<NumericAnalysisField, Range> = {
  analysisDefaultCount: { min: 1, max: 1000, step: 1, integer: true },
  analysisBodyMaxChars: { min: 500, max: 50_000, step: 500, integer: true },
  analysisRepresentativesPerGroup: { min: 2, max: 10, step: 1, integer: true },
  analysisMinGroupSize: { min: 2, max: 50, step: 1, integer: true },
  analysisDerivedConfidencePenalty: { min: 0, max: 0.5, step: 0.01, integer: false },
  analysisClusterDistance: { min: 0.02, max: 0.6, step: 0.01, integer: false },
  analysisMemoryMinApprovals: { min: 1, max: 20, step: 1, integer: true },
  bulkApproveThreshold: { min: 0.5, max: 1, step: 0.01, integer: false },
};

export const MAX_PROMPT_TEMPLATE_LENGTH = 20_000;

/** The placeholder the API requires in a custom prompt. */
export const EMAILS_PLACEHOLDER = '{{emails}}';

/** Placeholders the analysis prompt builder fills in. */
export const PROMPT_PLACEHOLDERS: readonly { name: string; help: string }[] = [
  { name: EMAILS_PLACEHOLDER, help: 'The emails to analyse (required).' },
  { name: '{{labelTree}}', help: 'Your existing Gmail labels.' },
  { name: '{{memory}}', help: 'Past decisions for similar senders.' },
  { name: '{{attachments}}', help: 'Attachment names and types.' },
  { name: '{{actionLabel}}', help: 'The action label name from Settings.' },
  { name: '{{deleteLabel}}', help: 'The delete label name from Settings.' },
];

export const GROUPING_MODES: readonly {
  value: AnalysisGroupingMode;
  label: string;
  help: string;
}[] = [
  { value: 'off', label: 'Off', help: 'Every email gets its own LLM call.' },
  {
    value: 'sender_subject',
    label: 'Sender + subject',
    help: 'Emails from one sender with similar subjects share one call.',
  },
  {
    value: 'auto',
    label: 'Auto',
    help: 'Sender + subject, then similar emails by embedding when an embedding model is chosen.',
  },
];
