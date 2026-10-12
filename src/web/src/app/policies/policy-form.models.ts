import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { labelPathValidator } from '../review/labels.service';
import {
  EditPolicyRequest,
  label,
  MAX_RULE_NAME_LENGTH,
  PolicyAction,
  PolicyRuleDto,
  RuleMatchDto,
  SenderPolicyDetailDto,
} from './policies.models';

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
  extraLabels: FormControl<string[]>;
  mailType: FormControl<string>;
  retentionDays: FormControl<number | null>;
  action: FormControl<PolicyAction>;
}>;

export type PolicyForm = FormGroup<{
  topicLabel: FormControl<string>;
  documentTypeLabel: FormControl<string>;
  /** Always `[]` while mixed: the default has no topic. */
  extraLabels: FormControl<string[]>;
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
    extraLabels: new FormControl([...(rule?.extraLabels ?? [])], { nonNullable: true }),
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
  const form: PolicyForm = new FormGroup({
    topicLabel: new FormControl(p.topicLabel ?? '', {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    documentTypeLabel: new FormControl(p.documentTypeLabel ?? '', {
      nonNullable: true,
      validators: [optionalLabelPath],
    }),
    extraLabels: new FormControl([...(p.extraLabels ?? [])], { nonNullable: true }),
    mailType: new FormControl(p.mailType ?? '', { nonNullable: true }),
    retentionDays: new FormControl(p.retentionDays, { validators: retentionValidators }),
    action: new FormControl<PolicyAction>(p.action, { nonNullable: true }),
    isMixed: new FormControl(p.isMixed, { nonNullable: true }),
    rules: new FormArray(
      [...detail.rules].sort((a, b) => a.position - b.position).map((r) => ruleForm(r)),
    ),
  });
  // A mixed policy's default has no topic, so nothing to add labels next to.
  form.controls.isMixed.valueChanges.subscribe((mixed) => {
    if (mixed) form.controls.extraLabels.setValue([]);
  });
  return form;
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

/** The `PUT` body: rule positions are the array order; blank text is null; extras always in full. */
export function editRequest(form: PolicyForm): EditPolicyRequest {
  const v = form.getRawValue();
  return {
    topicLabel: orNull(v.topicLabel),
    documentTypeLabel: orNull(v.documentTypeLabel),
    extraLabels: v.isMixed ? [] : v.extraLabels,
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
      extraLabels: r.extraLabels,
      mailType: orNull(r.mailType),
      retentionDays: r.retentionDays ?? null,
      action: r.action,
    })),
  };
}

/** A rule's outcome as one line, e.g. `Bills + Tax, Home · archive · receipt`. */
export function outcomeSummary(v: ReturnType<RuleForm['getRawValue']>): string {
  const topic = v.extraLabels.length
    ? `${v.topicLabel} + ${v.extraLabels.join(', ')}`
    : v.topicLabel;
  return [topic, v.action, label(v.mailType)].filter(Boolean).join(' · ');
}
