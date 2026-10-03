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

/** The full `SettingsDto`: the setup, analysis, attachment and Claude review fields. */
export type SettingsDto = AppSettings &
  AnalysisSettings &
  AttachmentsSettingsDto &
  ClaudeSettingsDto;

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
  /** The input's `min` attribute when it differs from `min`: HTML uses it as the step base. */
  inputMin?: number;
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

/** `AttachmentType` as the API serialises it (snake_case, enum order). */
export type AttachmentType =
  | 'pdf'
  | 'image'
  | 'spreadsheet'
  | 'csv'
  | 'word_document'
  | 'presentation'
  | 'plain_text'
  | 'archive'
  | 'other';

/** How image attachments are read (#72): local OCR or an Ollama vision model. */
export type AttachmentImageMode = 'ocr' | 'vision';

/** `AttachmentTypeSetting`. */
export interface AttachmentTypeSetting {
  type: AttachmentType;
  enabled: boolean;
}

/** `AttachmentSettings` (`attachments` in `GET /api/settings`). */
export interface AttachmentSettings {
  enabled: boolean;
  /** One entry per type in enum order; `archive` is always off. */
  types: AttachmentTypeSetting[];
  maxBytes: number;
  maxImageBytes: number;
  maxChars: number;
  maxPerMessage: number;
  /** Absent on an API without the image-reading choice. */
  imageMode?: AttachmentImageMode;
}

/** The attachment fields of `SettingsDto`; both absent on an older API. */
export interface AttachmentsSettingsDto {
  attachments?: AttachmentSettings;
  visionModel?: string | null;
}

/** `UpdateAttachmentSettingsRequest`: omitted values stay unchanged; `types` changes only the listed types. */
export type AttachmentSettingsUpdate = Partial<AttachmentSettings>;

/** The attachment part of `UpdateSettingsRequest`; an empty `visionModel` clears it. */
export interface AttachmentsUpdate {
  attachments?: AttachmentSettingsUpdate;
  visionModel?: string;
}

export const ARCHIVE_TYPE: AttachmentType = 'archive';

/** Label and description per type, in the API's enum order. */
export const ATTACHMENT_TYPES: Record<AttachmentType, { label: string; description: string }> = {
  pdf: { label: 'PDF (pdf)', description: 'The text layer; scanned pages are read like images.' },
  image: {
    label: 'Images (png, jpg, gif, webp, tiff, heic)',
    description: 'Text in the picture, read as chosen under "Reading images".',
  },
  spreadsheet: { label: 'Spreadsheets (xlsx, xls, ods)', description: 'Sheet names and cells.' },
  csv: { label: 'CSV (csv)', description: 'Rows as text.' },
  word_document: { label: 'Documents (docx, doc, odt, rtf)', description: 'The document text.' },
  presentation: { label: 'Presentations (pptx, ppt, odp)', description: 'Slide text.' },
  plain_text: { label: 'Plain text (txt, md)', description: 'The file as is.' },
  archive: { label: 'Archives (zip, 7z, rar, tar, gz)', description: 'Never opened.' },
  other: { label: 'Other files', description: 'Any type not listed above.' },
};

export type NumericAttachmentField = 'maxBytes' | 'maxImageBytes' | 'maxChars' | 'maxPerMessage';

export const BYTES_PER_MB = 1024 * 1024;

/**
 * Same bounds as the API's `SettingsValidation`, in the units the form shows: the byte limits are
 * in MB (64 KB–25 MB) and converted on load and save. Their input `min` is 0 so the spinner
 * moves in whole MB; the form validator keeps the 64 KB minimum.
 */
export const ATTACHMENT_LIMITS: Record<NumericAttachmentField, Range> = {
  maxBytes: { min: 0.0625, max: 25, step: 1, integer: false, inputMin: 0 },
  maxImageBytes: { min: 0.0625, max: 25, step: 1, integer: false, inputMin: 0 },
  maxChars: { min: 500, max: 50_000, step: 500, integer: true },
  maxPerMessage: { min: 1, max: 20, step: 1, integer: true },
};

export const ATTACHMENT_MB_FIELDS: readonly NumericAttachmentField[] = [
  'maxBytes',
  'maxImageBytes',
];

export const IMAGE_MODES: readonly { value: AttachmentImageMode; label: string; help: string }[] = [
  { value: 'ocr', label: 'Local OCR (default)', help: 'Reads the text in images on this machine.' },
  {
    value: 'vision',
    label: 'Ollama vision model',
    help: 'A vision-capable model transcribes and describes each image; slower.',
  },
];

/** Ollama's capability name for models that accept images. */
export const VISION_CAPABILITY = 'vision';

/** `ClaudeReviewerMode` as the API serialises it. */
export type ClaudeReviewerMode = 'off' | 'headless_claude_code' | 'claude_desktop';

/** The Claude review fields of `SettingsDto` (DESIGN §6.7, §8.10). */
export interface ClaudeSettings {
  claudeReviewerMode: ClaudeReviewerMode;
  claudeSuggestLowConfidence: boolean;
  claudeSuggestThreshold: number;
  claudeSuggestNewLabels: boolean;
  claudeRunTimeoutSeconds: number;
  claudeMaxItemsPerRun: number;
  claudeMaxTurns: number;
  /** `null` means the subscription's default model. */
  claudeModel: string | null;
  /** Whether `CLAUDE_CODE_OAUTH_TOKEN` is set in `.env`; the value never reaches the web. */
  claudeTokenSet: boolean;
}

/** The Claude fields of `SettingsDto`; all absent on an API without Claude review. */
export type ClaudeSettingsDto = Partial<ClaudeSettings>;

/** The Claude part of `UpdateSettingsRequest`; an empty `claudeModel` clears it. */
export type ClaudeSettingsUpdate = Partial<
  Omit<ClaudeSettings, 'claudeTokenSet' | 'claudeModel'> & { claudeModel: string }
>;

export type NumericClaudeField =
  'claudeSuggestThreshold' | 'claudeRunTimeoutSeconds' | 'claudeMaxItemsPerRun' | 'claudeMaxTurns';

/** Same bounds as the API's `SettingsValidation`; `step` only sets the arrow-key increment. */
export const CLAUDE_LIMITS: Record<NumericClaudeField, Range> = {
  claudeSuggestThreshold: { min: 0.3, max: 0.95, step: 0.05, integer: false },
  claudeRunTimeoutSeconds: { min: 60, max: 3600, step: 60, integer: true },
  claudeMaxItemsPerRun: { min: 1, max: 50, step: 1, integer: true },
  claudeMaxTurns: { min: 10, max: 300, step: 10, integer: true },
};

export const CLAUDE_MODES: readonly { value: ClaudeReviewerMode; label: string; help: string }[] = [
  { value: 'off', label: 'Off', help: 'No Claude second opinion; the Claude actions stay hidden.' },
  {
    value: 'headless_claude_code',
    label: 'Claude Code (headless)',
    help: 'The api runs the Claude Code CLI for each batch you send; needs the token in .env.',
  },
  {
    value: 'claude_desktop',
    label: 'Claude Desktop',
    help: 'You open Claude Desktop and paste the review prompt; it reaches the organiser over MCP.',
  },
];

/** The token status chip; the token itself is never sent to the web. */
export const CLAUDE_TOKEN_STATUS = {
  set: 'Token set in .env',
  missing: 'CLAUDE_CODE_OAUTH_TOKEN not set — run claude setup-token',
} as const;
