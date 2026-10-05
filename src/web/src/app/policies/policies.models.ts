import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { ParamMap, Params } from '@angular/router';
import { labelPathValidator } from '../review/labels.service';

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
  mailType: string | null;
  retentionDays: number | null;
  action: PolicyAction;
}

export interface EditPolicyRequest {
  topicLabel: string | null;
  documentTypeLabel: string | null;
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

export type MatchForm = FormGroup<{
  fromAddress: FormControl<string>;
  fromSubdomain: FormControl<string>;
  subjectTemplate: FormControl<string>;
  subjectContains: FormControl<string>;
  category: FormControl<string>;
  /** `''` any, `'yes'` or `'no'`. */
  listIdPresent: FormControl<TriState>;
  listUnsubscribePresent: FormControl<TriState>;
}>;
export type TriState = '' | 'yes' | 'no';

export type RuleForm = FormGroup<{
  /** Null for a rule added here. */
  id: FormControl<string | null>;
  name: FormControl<string>;
  match: MatchForm;
  topicLabel: FormControl<string>;
  documentTypeLabel: FormControl<string>;
  mailType: FormControl<string>;
  retentionDays: FormControl<number | null>;
  action: FormControl<PolicyAction>;
}>;

export type PolicyForm = FormGroup<{
  topicLabel: FormControl<string>;
  documentTypeLabel: FormControl<string>;
  mailType: FormControl<string>;
  retentionDays: FormControl<number | null>;
  action: FormControl<PolicyAction>;
  isMixed: FormControl<boolean>;
  rules: FormArray<RuleForm>;
}>;

const retentionValidators = [Validators.min(1), Validators.pattern(/^\d+$/)];

/** `labelPathValidator` for a label that may be left blank. */
export function optionalLabelPath(control: AbstractControl): ValidationErrors | null {
  return String(control.value ?? '').trim() ? labelPathValidator(control) : null;
}

/** At least one match field is set (the API's `RuleMatch.IsEmpty`). */
export function matchRequired(control: AbstractControl): ValidationErrors | null {
  const v = (control as MatchForm).getRawValue();
  const set = Object.values(v).some((x) => typeof x === 'string' && x.trim() !== '');
  return set ? null : { matchEmpty: 'Set at least one match field.' };
}

export function ruleForm(rule?: PolicyRuleDto): RuleForm {
  const m = rule?.match;
  const text = (v: string | null | undefined) => new FormControl(v ?? '', { nonNullable: true });
  const tri = (v: boolean | null | undefined) =>
    new FormControl<TriState>(v === true ? 'yes' : v === false ? 'no' : '', { nonNullable: true });
  return new FormGroup({
    id: new FormControl(rule?.id ?? null),
    name: new FormControl(rule?.name ?? '', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(MAX_RULE_NAME_LENGTH)],
    }),
    match: new FormGroup(
      {
        fromAddress: text(m?.fromAddress),
        fromSubdomain: text(m?.fromSubdomain),
        subjectTemplate: text(m?.subjectTemplate),
        subjectContains: text(m?.subjectContains),
        category: text(m?.category),
        listIdPresent: tri(m?.listIdPresent),
        listUnsubscribePresent: tri(m?.listUnsubscribePresent),
      },
      { validators: matchRequired },
    ),
    topicLabel: new FormControl(rule?.topicLabel ?? '', {
      nonNullable: true,
      validators: [labelPathValidator],
    }),
    documentTypeLabel: new FormControl(rule?.documentTypeLabel ?? '', {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    mailType: new FormControl(rule?.mailType ?? '', { nonNullable: true }),
    retentionDays: new FormControl(rule?.retentionDays ?? null, {
      validators: retentionValidators,
    }),
    action: new FormControl<PolicyAction>(rule?.action ?? 'keep', { nonNullable: true }),
  });
}

/** The edit form for a policy and its rules in saved order. */
export function policyForm(detail: SenderPolicyDetailDto): PolicyForm {
  const p = detail.policy;
  return new FormGroup({
    topicLabel: new FormControl(p.topicLabel ?? '', {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    documentTypeLabel: new FormControl(p.documentTypeLabel ?? '', {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    mailType: new FormControl(p.mailType ?? '', { nonNullable: true }),
    retentionDays: new FormControl(p.retentionDays, { validators: retentionValidators }),
    action: new FormControl<PolicyAction>(p.action, { nonNullable: true }),
    isMixed: new FormControl(p.isMixed, { nonNullable: true }),
    rules: new FormArray(
      [...detail.rules].sort((a, b) => a.position - b.position).map((r) => ruleForm(r)),
    ),
  });
}

/** Moves the rule at `index` by `delta` places; out-of-range moves do nothing. */
export function moveRule(rules: FormArray<RuleForm>, index: number, delta: -1 | 1): void {
  const target = index + delta;
  if (index < 0 || target < 0 || index >= rules.length || target >= rules.length) return;
  const rule = rules.at(index);
  rules.removeAt(index, { emitEvent: false });
  rules.insert(target, rule);
  rules.markAsDirty();
}

const orNull = (text: string) => text.trim() || null;
const triValue = (v: TriState) => (v === 'yes' ? true : v === 'no' ? false : null);

/** A rule form's match as the API takes it; blank fields are null. */
export function matchValue(m: ReturnType<MatchForm['getRawValue']>): RuleMatchDto {
  return {
    fromAddress: orNull(m.fromAddress),
    fromSubdomain: orNull(m.fromSubdomain),
    subjectTemplate: orNull(m.subjectTemplate),
    subjectContains: orNull(m.subjectContains),
    category: orNull(m.category),
    listIdPresent: triValue(m.listIdPresent),
    listUnsubscribePresent: triValue(m.listUnsubscribePresent),
  };
}

/** The `PUT` body: rule positions are the array order; blank text is null. */
export function editRequest(form: PolicyForm): EditPolicyRequest {
  const v = form.getRawValue();
  return {
    topicLabel: orNull(v.topicLabel),
    documentTypeLabel: orNull(v.documentTypeLabel),
    mailType: orNull(v.mailType),
    retentionDays: v.retentionDays ?? null,
    action: v.action,
    isMixed: v.isMixed,
    rules: v.rules.map((r) => ({
      id: r.id,
      name: r.name.trim(),
      match: matchValue(r.match),
      topicLabel: r.topicLabel.trim(),
      documentTypeLabel: orNull(r.documentTypeLabel),
      mailType: orNull(r.mailType),
      retentionDays: r.retentionDays ?? null,
      action: r.action,
    })),
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

/**
 * Puts each ProblemDetails field error (`topicLabel`, `rules[2].match`, …) on the form control it names,
 * or on its nearest named ancestor, until its value changes; returns the messages no control below the
 * form takes.
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
    const held = SERVER_ERRORS.get(control);
    const message = held ? `${held.message} ${messages.join(' ')}` : messages.join(' ');
    SERVER_ERRORS.set(control, { value: JSON.stringify(control.value), message });
    if (!control.hasValidator(serverErrorValidator)) control.addValidators(serverErrorValidator);
    control.updateValueAndValidity();
    control.markAsTouched();
  }
  return unplaced;
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
