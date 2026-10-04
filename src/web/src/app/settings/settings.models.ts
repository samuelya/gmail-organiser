import { AbstractControl, ValidationErrors } from '@angular/forms';
import { AppSettings } from '../setup/setup.service';
import { labelPathError } from '../review/labels.service';

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

/**
 * The full `SettingsDto`: the setup, analysis, attachment, Claude review, protection and Apps Script
 * fields.
 */
export type SettingsDto = AppSettings &
  AnalysisSettings &
  AttachmentsSettingsDto &
  ClaudeSettingsDto &
  ProtectionSettingsDto &
  AppsScriptSettingsDto;

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

/** `ProtectionSettings` (`protection` in `GET /api/settings`, #176): which rules keep mail off `To-Be-Deleted`. */
export interface ProtectionSettings {
  attachments: boolean;
  starred: boolean;
  important: boolean;
  repliedThreads: boolean;
  /** Mail from these domains, or their subdomains, is never marked for deletion (#203). */
  allowlistedDomains: string[];
}

/** The protection field of `SettingsDto`; absent on an older API. */
export interface ProtectionSettingsDto {
  protection?: ProtectionSettings;
}

/** The protection part of `UpdateSettingsRequest`: omitted rules stay unchanged. */
export interface ProtectionUpdate {
  protection?: Partial<ProtectionSettings>;
}

export type ProtectionRule = Exclude<keyof ProtectionSettings, 'allowlistedDomains'>;

/** The API's limits for `protection.allowlistedDomains`. */
export const MAX_ALLOWLISTED_DOMAINS = 500;
const MAX_DOMAIN_LENGTH = 253;
const HOST_NAME = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/;

/**
 * A domain as the API stores it, or null where it answers 400: trimmed, one trailing dot dropped,
 * lower-case, an international name in punycode; a host name with a dot, no `@`, not an IP.
 */
export function normaliseAllowlistDomain(value: string): string | null {
  let domain = value.trim();
  if (domain.endsWith('.')) domain = domain.slice(0, -1);
  if (!domain || domain.includes('@') || /[\s/:?#\\[\]]/.test(domain)) return null;
  try {
    // The URL parser applies the same IDNA mapping as the API's IdnMapping.
    domain = new URL(`http://${domain}`).hostname;
  } catch {
    return null;
  }
  if (domain.length > MAX_DOMAIN_LENGTH || !HOST_NAME.test(domain)) return null;
  // An all-numeric last label is an IPv4 address, which the API rejects.
  return /\.\d+$/.test(domain) ? null : domain;
}

export function isAllowlistDomain(value: string): boolean {
  return normaliseAllowlistDomain(value) !== null;
}

/** The listed domain that covers `domain`: itself, or a parent domain; null when none does. */
export function coveringDomain(domain: string, listed: readonly string[]): string | null {
  const host = domain.toLowerCase();
  return listed.find((d) => host === d || host.endsWith(`.${d}`)) ?? null;
}

/** Label and one-line hint per rule, in the API's order. */
export const PROTECTION_RULES: readonly { key: ProtectionRule; label: string; hint: string }[] = [
  {
    key: 'attachments',
    label: 'Attachments',
    hint: 'Emails with an attachment are never marked for deletion.',
  },
  { key: 'starred', label: 'Starred', hint: 'Starred emails are never marked for deletion.' },
  {
    key: 'important',
    label: 'Important',
    hint: 'Emails Gmail marks important are never marked for deletion.',
  },
  {
    key: 'repliedThreads',
    label: 'Replied threads',
    hint: 'Emails in a thread you replied to are never marked for deletion.',
  },
];

/** `ArchiveRule`: archive inbox threads with `label` once their last message is `days` old (#210). */
export interface ArchiveRule {
  label: string;
  days: number;
}

/** `AppsScriptSettings` (`appsScript` in `GET /api/settings`): the auto-archive script's config. */
export interface AppsScriptSettings {
  rules: ArchiveRule[];
  /** Archive labelled inbox threads that no longer carry the action label. */
  actionDoneArchive: boolean;
  /** Labels whose threads the script never archives. */
  keepInInboxLabels: string[];
  /** The script only logs what it would archive. */
  dryRun: boolean;
}

/** The Apps Script field of `SettingsDto`; absent on an older API. */
export interface AppsScriptSettingsDto {
  appsScript?: AppsScriptSettings;
}

/** The Apps Script part of `UpdateSettingsRequest`: the block is replaced whole, so always send all of it. */
export interface AppsScriptUpdate {
  appsScript: AppsScriptSettings;
}

/** `GET /api/rules/apps-script/config`: the script's `CONFIG` block for the saved settings. */
export interface AppsScriptConfigDto {
  scriptVersion: number;
  config: string;
  generatedAt: string;
}

/** The API's `SettingsValidation` limits for the Apps Script block. */
export const ARCHIVE_RULE_DAYS: Range = { min: 1, max: 3650, step: 1, integer: true };
export const MAX_ARCHIVE_RULES = 100;
export const MAX_KEEP_IN_INBOX_LABELS = 50;

/** The API's `ScriptSearchableLabel`: what the script's `label:` search can match. */
const SCRIPT_SEARCHABLE_LABEL = /^[\p{L}\p{N}_/][\p{L}\p{N}_/ -]*$/u;

/** Why the API would refuse `path` as an Apps Script label, or null when it is valid. */
export function scriptLabelError(path: string): string | null {
  const error = labelPathError(path);
  if (error) return error;
  return SCRIPT_SEARCHABLE_LABEL.test(path.trim())
    ? null
    : "Only letters, digits, spaces, '_', '-' and '/', not starting with '-'.";
}

/** Gmail matches labels ignoring case and searches "A B" as "A-B": equal keys are the same label. */
export function scriptLabelKey(path: string): string {
  return path.trim().replace(/ /g, '-').toLowerCase();
}

/** The two configurable label names of `SettingsDto` (#198). */
export interface LabelSettings {
  actionLabelName: string;
  deleteLabelName: string;
}

/** The label part of `UpdateSettingsRequest`: omitted names stay unchanged. */
export type LabelSettingsUpdate = Partial<LabelSettings>;

/** Same bound as the API's `MaxLabelNameLength`. */
export const MAX_LABEL_NAME_LENGTH = 225;

/** The API field a document-type parent clash comes back under; this page has no field for it. */
export const DOCUMENT_TYPE_PARENT_FIELD = 'documentTypeParent';

/**
 * A Gmail label path: `/`-separated segments, none blank, no leading or trailing `/`. Blank is left
 * to `Validators.required`; the API checks the rest (system labels, segment count and length).
 */
export function labelPathValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value?.trim() ?? '';
  if (!value) return null;
  return value.split('/').every((segment) => segment.trim().length > 0)
    ? null
    : { labelPath: true };
}

/** The action and delete labels must differ ignoring case, as Gmail label names do; set on the group. */
export function labelsDifferValidator(group: AbstractControl): ValidationErrors | null {
  const { actionLabelName, deleteLabelName } = group.value as Partial<LabelSettings>;
  const action = actionLabelName?.trim().toLowerCase();
  const del = deleteLabelName?.trim().toLowerCase();
  return action && del && action === del ? { labelsMatch: true } : null;
}

/** `PurgeRequest.ConfirmationWord`: what the user types to purge local data. */
export const PURGE_CONFIRMATION_WORD = 'purge';

/** `PurgeResponse` from `POST /api/settings/purge`. */
export interface PurgeResponse {
  tables: string[];
  purgedAt: string;
}
