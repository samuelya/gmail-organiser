import { AbstractControl, ValidationErrors } from '@angular/forms';
import { ParamMap, Params } from '@angular/router';
import type { PolicyForm } from './policy-form.models';

/** `PolicyApplyJob.JobType`: the job an approve or "Apply again" queues. */
export const POLICY_APPLY_JOB = 'policy_apply';

export const POLICY_STATUSES = ['proposed', 'approved', 'rejected'] as const;
export type PolicyStatus = (typeof POLICY_STATUSES)[number];
export type PolicyScope = 'sender' | 'domain' | 'list';
export type PolicyAction = 'keep' | 'archive' | 'delete' | 'unsubscribe';
export const POLICY_ACTIONS: readonly PolicyAction[] = ['keep', 'archive', 'delete', 'unsubscribe'];
/** The API's snake_case `MailType` names. */
export const MAIL_TYPES: readonly string[] = [
  'personal',
  'action_bill',
  'receipt',
  'statement_document',
  'account_alert',
  'notification',
  'newsletter',
  'marketing',
  'social',
  'security_otp',
];
/** The API's snake_case `MessageCategory` names. */
export const MESSAGE_CATEGORIES: readonly string[] = [
  'primary',
  'social',
  'promotions',
  'updates',
  'forums',
];
export const PAGE_SIZES: readonly number[] = [25, 50, 100];
/** The API's `PolicyQuery.MaxSearchLength` and `PolicyService.MaxRuleNameLength`. */
export const MAX_SEARCH_LENGTH = 200;
export const MAX_RULE_NAME_LENGTH = 200;
const MAX_PAGE = 100_000;

/** `SenderPolicyDto`: a policy in the review list. */
export interface SenderPolicyDto {
  id: string;
  scope: PolicyScope;
  scopeKey: string;
  displayName: string | null;
  isMixed: boolean;
  topicLabel: string | null;
  documentTypeLabel: string | null;
  /** Labels added next to the topic; missing from older APIs. */
  extraLabels?: string[];
  mailType: string | null;
  retentionDays: number | null;
  action: PolicyAction;
  confidence: number;
  reason: string;
  model: string | null;
  promptVersion: string | null;
  status: PolicyStatus;
  edited: boolean;
  createdAt: string;
  decidedAt: string | null;
  appliedAt: string | null;
  ruleCount: number;
  messageCount: number;
  kind: string;
  /** 0–1 of `messageCount`. */
  unreadRatio: number;
}

/** `RuleMatchDto`: null fields do not take part in the match. */
export interface RuleMatchDto {
  listIdPresent: boolean | null;
  listUnsubscribePresent: boolean | null;
  fromAddress: string | null;
  fromSubdomain: string | null;
  category: string | null;
  subjectTemplate: string | null;
  subjectContains: string | null;
}

/** `PolicyRuleDto`: a sub-rule with its preview over the sampled messages. */
export interface PolicyRuleDto {
  id: string;
  position: number;
  name: string;
  match: RuleMatchDto;
  topicLabel: string;
  documentTypeLabel: string | null;
  extraLabels?: string[];
  mailType: string | null;
  retentionDays: number | null;
  action: PolicyAction;
  status: PolicyStatus;
  source: string;
  reason: string;
  matchCount: number;
  sampleSubjects: string[];
}

export interface CategoryMixDto {
  primary: number;
  promotions: number;
  social: number;
  updates: number;
  forums: number;
  none: number;
}

/** `SenderTemplateDto`: one subject template of the sender profile. */
export interface SenderTemplateDto {
  template: string;
  count: number;
  exampleSubject: string | null;
  listIdPresent: boolean;
  listUnsubscribePresent: boolean;
  categoryMix: CategoryMixDto;
  attachmentRatio: number;
  unreadRatio: number;
  precedence: string | null;
}

/** `SenderPolicyDetailDto`: every sampled message is in one rule's count or in default, unmatched or guarded. */
export interface SenderPolicyDetailDto {
  policy: SenderPolicyDto;
  rules: PolicyRuleDto[];
  defaultCount: number;
  unmatchedCount: number;
  guardedCount: number;
  sampledMessages: number;
  sampled: boolean;
  profileTemplates: SenderTemplateDto[];
}

export interface EditPolicyRuleRequest {
  id: string | null;
  name: string;
  match: RuleMatchDto;
  topicLabel: string;
  documentTypeLabel: string | null;
  /** The full list; `[]` clears it. */
  extraLabels: string[];
  mailType: string | null;
  retentionDays: number | null;
  action: PolicyAction;
}

export interface EditPolicyRequest {
  topicLabel: string | null;
  documentTypeLabel: string | null;
  /** The full list; `[]` clears it, as a mixed policy's default must. */
  extraLabels: string[];
  mailType: string | null;
  retentionDays: number | null;
  action: PolicyAction;
  isMixed: boolean;
  rules: EditPolicyRuleRequest[];
}

/** `EditPolicyResponse`: `reapply` when an approved policy changed and must be applied again. */
export interface EditPolicyResponse {
  policy: SenderPolicyDetailDto;
  reapply: boolean;
}

/** `ApprovePolicyResponse`: `jobId` is the queued apply job. */
export interface ApprovePolicyResponse {
  policy: SenderPolicyDto;
  jobId: string | null;
}

export interface PolicyQuery {
  status: PolicyStatus;
  search: string;
  page: number;
  pageSize: number;
}

export const DEFAULT_POLICY_QUERY: Readonly<PolicyQuery> = {
  status: 'proposed',
  search: '',
  page: 1,
  pageSize: 25,
};

/** The list query from the URL; invalid values fall back to the defaults. */
export function parsePolicyQuery(params: ParamMap): PolicyQuery {
  const d = DEFAULT_POLICY_QUERY;
  const status = params.get('status') as PolicyStatus | null;
  const page = Number(params.get('page'));
  const pageSize = Number(params.get('pageSize'));
  return {
    status: status && POLICY_STATUSES.includes(status) ? status : d.status,
    search: cleanSearch(params.get('search') ?? ''),
    page: Number.isInteger(page) && page >= 1 && page <= MAX_PAGE ? page : d.page,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : d.pageSize,
  };
}

/** URL query params for a query; defaults are left out (`null` removes them when merging). */
export function policyQueryParams(query: PolicyQuery): Params {
  const d = DEFAULT_POLICY_QUERY;
  return {
    status: query.status === d.status ? null : query.status,
    search: query.search || null,
    page: query.page === d.page ? null : query.page,
    pageSize: query.pageSize === d.pageSize ? null : query.pageSize,
  };
}

// eslint-disable-next-line no-control-regex
const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/g;

/** Trimmed, without control characters, at most the API's length. */
export function cleanSearch(text: string): string {
  return text.replace(CONTROL_CHARS, '').trim().slice(0, MAX_SEARCH_LENGTH);
}

/** A rule's match as one line, e.g. `subject contains "invoice" · List-Id`. */
export function matchSummary(match: RuleMatchDto): string {
  const parts: string[] = [];
  if (match.fromAddress) parts.push(`from ${match.fromAddress}`);
  if (match.fromSubdomain) parts.push(`from *.${match.fromSubdomain}`);
  if (match.subjectTemplate) parts.push(`template "${match.subjectTemplate}"`);
  if (match.subjectContains) parts.push(`subject contains "${match.subjectContains}"`);
  if (match.category) parts.push(`category ${label(match.category)}`);
  if (match.listIdPresent !== null) parts.push(match.listIdPresent ? 'List-Id' : 'no List-Id');
  if (match.listUnsubscribePresent !== null)
    parts.push(match.listUnsubscribePresent ? 'unsubscribe link' : 'no unsubscribe link');
  return parts.join(' · ') || 'Matches nothing yet';
}

/** A snake_case API name for display: `action_bill` → `action bill`. */
export function label(name: string | null | undefined): string {
  return name ? name.replaceAll('_', ' ') : '';
}

/** What the approve dialog states: the messages the policy decides and those left for analysis. */
export interface ApproveSummary {
  matched: number;
  unmatched: number;
  guarded: number;
  sampledMessages: number;
  sampled: boolean;
  mixed: boolean;
}

/** Matched = the default's count plus every non-rejected rule's (the API counts a rejected rule as 0). */
export function approveSummary(detail: SenderPolicyDetailDto): ApproveSummary {
  return {
    matched: detail.defaultCount + detail.rules.reduce((sum, r) => sum + r.matchCount, 0),
    unmatched: detail.unmatchedCount,
    guarded: detail.guardedCount,
    sampledMessages: detail.sampledMessages,
    sampled: detail.sampled,
    mixed: detail.policy.isMixed,
  };
}

/** Server field errors by control, held with the value they were given for. */
const SERVER_ERRORS = new WeakMap<AbstractControl, { value: string; message: string }>();

/** `{ server }` while the control keeps the value the server refused; survives re-validation. */
function serverErrorValidator(control: AbstractControl): ValidationErrors | null {
  const held = SERVER_ERRORS.get(control);
  if (!held) return null;
  if (held.value === JSON.stringify(control.value)) return { server: held.message };
  SERVER_ERRORS.delete(control);
  return null;
}

/** Controls the page never shows: an error on one could neither be seen nor cleared. */
const HIDDEN_CONTROLS = new Set(['id']);

/** The form-level text for an error on a rule's hidden id: the rules changed since this page loaded. */
export const STALE_RULES_ERROR = 'The rules changed elsewhere; reload to continue.';

/**
 * Puts each ProblemDetails field error (`topicLabel`, `rules[2].match`, …) on the form control it names,
 * or on its nearest named ancestor, until its value changes; returns the messages no visible control
 * below the form takes.
 */
export function applyServerErrors(form: PolicyForm, errors: Record<string, string[]>): string[] {
  const unplaced: string[] = [];
  for (const [key, messages] of Object.entries(errors)) {
    let path = key.replace(/\[(\d+)\]/g, '.$1').split('.');
    let control: AbstractControl | null = null;
    while (path.length && !(control = form.get(path))) path = path.slice(0, -1);
    if (!control || control === form) {
      unplaced.push(messages.join(' '));
      continue;
    }
    if (HIDDEN_CONTROLS.has(path[path.length - 1])) {
      const rule = path[0] === 'rules' && path.length === 3 ? `Rule ${Number(path[1]) + 1}: ` : '';
      unplaced.push(`${rule}${messages.join(' ')} ${STALE_RULES_ERROR}`);
      continue;
    }
    const held = SERVER_ERRORS.get(control);
    const message = held ? `${held.message} ${messages.join(' ')}` : messages.join(' ');
    SERVER_ERRORS.set(control, { value: JSON.stringify(control.value), message });
    if (!control.hasValidator(serverErrorValidator)) control.addValidators(serverErrorValidator);
    control.updateValueAndValidity();
    control.markAsTouched();
  }
  return unplaced;
}

/** The server's message for the control, if it still holds the refused value. */
export function serverErrorOf(control: AbstractControl): string | null {
  return (control.errors?.['server'] as string | undefined) ?? null;
}

/** A control's first error message: the server's, or the client validator's. */
export function controlError(control: AbstractControl): string | null {
  const e = control.errors;
  if (!e || !control.touched) return null;
  if (e['server']) return e['server'] as string;
  if (e['labelPath']) return e['labelPath'] as string;
  if (e['matchEmpty']) return e['matchEmpty'] as string;
  if (e['required']) return 'Required.';
  if (e['maxlength'])
    return `At most ${(e['maxlength'] as { requiredLength: number }).requiredLength} characters.`;
  if (e['min'] || e['pattern']) return 'A positive whole number of days, or blank for the default.';
  return 'Invalid.';
}

/** The autocomplete's options containing `text` (case-insensitive), at most `limit`. */
export function matchingOptions(options: readonly string[], text: string, limit = 50): string[] {
  const t = text.trim().toLowerCase();
  return (t ? options.filter((o) => o.toLowerCase().includes(t)) : options).slice(0, limit);
}
