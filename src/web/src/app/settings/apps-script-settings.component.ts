import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, debounceTime, defer, filter, map, of, Subject } from 'rxjs';
import { repoDocUrl } from '../core/repo-links';
import { LabelTreePicker } from '../review/label-tree-picker.component';
import { LabelDto } from '../review/labels.models';
import { LabelsService } from '../review/labels.service';
import {
  ARCHIVE_RULE_DAYS,
  AppsScriptConfigDto,
  AppsScriptSettings,
  MAX_ARCHIVE_RULES,
  MAX_KEEP_IN_INBOX_LABELS,
  scriptLabelError,
  scriptLabelKey,
} from './settings.models';
import { SettingsService } from './settings.service';

/** Edits settle this long before the whole block is saved. */
export const APPS_SCRIPT_SAVE_DEBOUNCE_MS = 600;
/** The install guide in the repository. */
export const APPS_SCRIPT_GUIDE_PATH = 'docs/setup/apps-script.md';
/** The days a new rule starts with. */
const NEW_RULE_DAYS = 30;

type RuleForm = FormGroup<{ label: FormControl<string>; days: FormControl<number | null> }>;
type Picker = 'rule' | 'keep';

/**
 * The Settings page's "Apps Script" section (#217): the archive rules, the two toggles and the
 * keep-in-inbox labels, saved whole (the API replaces the block) once edits settle, and the
 * generated `CONFIG` block to paste into the script. Loads and saves itself.
 */
@Component({
  selector: 'app-apps-script-settings',
  imports: [
    NgTemplateOutlet,
    ReactiveFormsModule,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSlideToggleModule,
    LabelTreePicker,
  ],
  templateUrl: './apps-script-settings.component.html',
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .error {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-error);
    }
    .config {
      font-family: ui-monospace, monospace;
      font-size: 0.8125rem;
      background: var(--mat-sys-surface-container);
      border: 1px solid var(--mat-sys-outline-variant);
    }
    th {
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);
      text-align: left;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppsScriptSettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly labelsApi = inject(LabelsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly days = ARCHIVE_RULE_DAYS;
  readonly maxRules = MAX_ARCHIVE_RULES;
  readonly maxKeep = MAX_KEEP_IN_INBOX_LABELS;
  readonly guideUrl = repoDocUrl(APPS_SCRIPT_GUIDE_PATH);

  readonly loaded = signal(false);
  readonly form = new FormGroup({
    rules: new FormArray<RuleForm>([]),
    actionDoneArchive: new FormControl(true, { nonNullable: true }),
    dryRun: new FormControl(true, { nonNullable: true }),
  });
  readonly rules = this.form.controls.rules;
  readonly keepInInboxLabels = signal<readonly string[]>([]);
  /** Field messages of the last 400, keyed as the API names them. */
  readonly serverErrors = signal<readonly string[]>([]);
  readonly saving = signal(0);

  readonly newRule = new FormGroup({
    label: new FormControl('', {
      nonNullable: true,
      validators: [
        scriptLabelValidator,
        this.uniqueAmong(() => this.rules.value.map((r) => r.label ?? '')),
      ],
    }),
    days: new FormControl<number | null>(NEW_RULE_DAYS, daysValidators()),
  });
  readonly newKeepForm = new FormGroup({
    label: new FormControl('', {
      nonNullable: true,
      validators: [scriptLabelValidator, this.uniqueAmong(() => this.keepInInboxLabels())],
    }),
  });
  readonly newKeep = this.newKeepForm.controls.label;

  readonly labels = signal<readonly LabelDto[] | null>(null);
  readonly labelsFailed = signal(false);
  readonly picker = signal<Picker | null>(null);

  readonly config = signal<AppsScriptConfigDto | null>(null);
  readonly configFailed = signal(false);

  private readonly edits = new Subject<void>();

  constructor() {
    this.form.disable();
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.edits.next());
    this.edits
      .pipe(
        debounceTime(APPS_SCRIPT_SAVE_DEBOUNCE_MS),
        filter(() => this.loaded() && this.form.valid),
        // The block as it is when the save starts: a queued save sends the latest edits.
        concatMap(() => defer(() => this.save(this.value()))),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  ngOnInit(): void {
    this.settingsApi
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the section stays disabled.
      .subscribe({ next: (settings) => this.load(settings.appsScript), error: () => undefined });
    this.labelsApi
      .labels()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (labels) => this.labels.set(labels.filter((l) => l.type === 'user')),
        error: () => this.labelsFailed.set(true),
      });
    this.loadConfig();
  }

  /** The whole block as the form holds it, labels trimmed. */
  value(): AppsScriptSettings {
    const { actionDoneArchive, dryRun } = this.form.getRawValue();
    return {
      rules: this.rules.getRawValue().map((r) => ({ label: r.label.trim(), days: r.days ?? 0 })),
      actionDoneArchive,
      keepInInboxLabels: [...this.keepInInboxLabels()],
      dryRun,
    };
  }

  addRule(): void {
    this.newRule.controls.label.updateValueAndValidity();
    if (this.newRule.invalid || !this.loaded() || this.rules.length >= this.maxRules) {
      this.newRule.markAllAsTouched();
      return;
    }
    const { label, days } = this.newRule.getRawValue();
    this.rules.push(ruleForm(label.trim(), days));
    this.newRule.reset({ label: '', days: NEW_RULE_DAYS });
    this.picker.set(null);
  }

  removeRule(index: number): void {
    this.rules.removeAt(index);
    this.newRule.controls.label.updateValueAndValidity();
  }

  addKeep(): void {
    this.newKeep.updateValueAndValidity();
    if (this.newKeep.invalid || !this.loaded() || this.keepInInboxLabels().length >= this.maxKeep) {
      this.newKeep.markAsTouched();
      return;
    }
    this.keepInInboxLabels.update((list) => [...list, this.newKeep.value.trim()]);
    this.newKeep.reset();
    this.picker.set(null);
    this.edits.next();
  }

  removeKeep(label: string): void {
    this.keepInInboxLabels.update((list) => list.filter((l) => l !== label));
    this.newKeep.updateValueAndValidity();
    this.edits.next();
  }

  togglePicker(picker: Picker): void {
    this.picker.update((open) => (open === picker ? null : picker));
  }

  pick(picker: Picker, path: string): void {
    const control = picker === 'rule' ? this.newRule.controls.label : this.newKeep;
    control.setValue(path);
    control.markAsTouched();
    this.picker.set(null);
  }

  copy(): void {
    const text = this.config()?.config;
    if (!text) return;
    navigator.clipboard.writeText(text).then(
      () => this.snackBar.open('Copied', undefined, { duration: 3000 }),
      () =>
        this.snackBar.open('Could not copy; select the text instead', undefined, {
          duration: 4000,
        }),
    );
  }

  loadConfig(): void {
    this.configFailed.set(false);
    this.settingsApi
      .appsScriptConfig()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the block offers a retry.
      .subscribe({
        next: (config) => this.config.set(config),
        error: () => this.configFailed.set(true),
      });
  }

  private save(appsScript: AppsScriptSettings) {
    this.saving.update((n) => n + 1);
    return this.settingsApi.saveAppsScript({ appsScript }).pipe(
      map(() => {
        this.saving.update((n) => n - 1);
        this.serverErrors.set([]);
        this.snackBar.open('Apps Script settings saved', undefined, { duration: 3000 });
        this.loadConfig();
      }),
      // A 400 is listed under the table; the error interceptor shows anything else.
      catchError((error: unknown) => {
        this.saving.update((n) => n - 1);
        this.serverErrors.set(problemMessages(error));
        return of(undefined);
      }),
    );
  }

  private load(settings: AppsScriptSettings | undefined): void {
    if (!settings) return;
    this.rules.clear({ emitEvent: false });
    for (const rule of settings.rules)
      this.rules.push(ruleForm(rule.label, rule.days), { emitEvent: false });
    this.form.patchValue(
      { actionDoneArchive: settings.actionDoneArchive, dryRun: settings.dryRun },
      { emitEvent: false },
    );
    this.keepInInboxLabels.set(settings.keepInInboxLabels);
    this.form.enable({ emitEvent: false });
    this.loaded.set(true);
  }

  /** `{ duplicate: true }` when the value names a label already in `existing()`. */
  private uniqueAmong(existing: () => readonly string[]): ValidatorFn {
    return (control: AbstractControl<string>) => {
      const key = scriptLabelKey(control.value ?? '');
      return key && existing().some((l) => scriptLabelKey(l) === key) ? { duplicate: true } : null;
    };
  }
}

function ruleForm(label: string, days: number | null): RuleForm {
  return new FormGroup({
    label: new FormControl(label, { nonNullable: true }),
    days: new FormControl(days, daysValidators()),
  });
}

function daysValidators(): ValidatorFn[] {
  return [
    Validators.required,
    Validators.min(ARCHIVE_RULE_DAYS.min),
    Validators.max(ARCHIVE_RULE_DAYS.max),
    wholeNumber,
  ];
}

/** `min`/`max` accept 2.5; the API's integer field would reject it. */
function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  return control.value === null || Number.isInteger(control.value) ? null : { integer: true };
}

/** `scriptLabelError` as a validator: `{ scriptLabel: message }`. */
function scriptLabelValidator(control: AbstractControl<string>): ValidationErrors | null {
  const error = scriptLabelError(control.value ?? '');
  return error ? { scriptLabel: error } : null;
}

/** A 400 ValidationProblem as readable lines (`appsScript.rules[1].label` → "Rule 2 label: …"). */
function problemMessages(error: unknown): string[] {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return [];
  const body = error.error as { title?: string; errors?: Record<string, string[]> } | null;
  const lines = Object.entries(body?.errors ?? {}).flatMap(([field, messages]) =>
    messages.map((m) => `${fieldName(field)}${m}`),
  );
  return lines.length ? lines : body?.title ? [body.title] : [];
}

function fieldName(field: string): string {
  const rule = /^appsScript\.rules\[(\d+)\](?:\.(label|days))?$/.exec(field);
  if (rule) return `Rule ${Number(rule[1]) + 1}${rule[2] ? ` ${rule[2]}` : ''}: `;
  const keep = /^appsScript\.keepInInboxLabels\[(\d+)\]$/.exec(field);
  if (keep) return `Keep-in-inbox label ${Number(keep[1]) + 1}: `;
  return '';
}
