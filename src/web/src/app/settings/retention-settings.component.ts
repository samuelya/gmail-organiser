import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  OnInit,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
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
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, debounceTime, defer, map, of, Subject, take, tap } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { isActiveJob, JobDto, newerJob, progressPercent } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { humanise } from '../dashboard/fetch.models';
import { LabelsService } from '../review/labels.service';
import {
  ARCHIVE_RULE_DAYS,
  MAX_ARCHIVE_RULES,
  scriptLabelError,
  scriptLabelKey,
  SettingsDto,
} from './settings.models';
import { SettingsService } from './settings.service';
import {
  problemErrors,
  RETENTION_DAYS,
  RETENTION_JOB_TYPE,
  RetentionStatusDto,
  wholeNumber,
} from './triage-settings.models';

/** Edits settle this long before they are saved. */
export const RETENTION_SAVE_DEBOUNCE_MS = 600;
/** The label suggestions shown at most. */
const MAX_LABEL_OPTIONS = 50;
const NEW_RULE_DAYS = 30;

type RuleForm = FormGroup<{ label: FormControl<string>; days: FormControl<number | null> }>;

/**
 * The Settings page's "Retention" section (#371): the sweep toggle and days per mail type, "Run now"
 * with the sweep's progress and status, and the Apps Script retention rules. Loads and saves itself.
 */
@Component({
  selector: 'app-retention-settings',
  imports: [
    DatePipe,
    DecimalPipe,
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSlideToggleModule,
  ],
  templateUrl: './retention-settings.component.html',
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
    th {
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);
      text-align: left;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RetentionSettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly labelsApi = inject(LabelsService);
  private readonly jobs = inject(JobsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** The Apps Script retention rules were saved, so the script's config block changed. */
  readonly appsScriptSaved = output<void>();

  readonly range = RETENTION_DAYS;
  readonly ruleDays = ARCHIVE_RULE_DAYS;
  readonly maxRules = MAX_ARCHIVE_RULES;
  readonly typeLabel = humanise;

  readonly loaded = signal(false);
  readonly saving = signal(0);
  readonly enabled = new FormControl(false, { nonNullable: true });
  /** Retention as last saved: "Run now" needs it on. */
  readonly savedEnabled = signal(false);
  /** Mail types in the API's order. */
  readonly types = signal<readonly string[]>([]);
  readonly days = new FormGroup<Record<string, FormControl<number | null>>>({});
  private readonly dayEdits = new Subject<string>();
  private readonly pendingTypes = new Set<string>();

  readonly status = signal<RetentionStatusDto | null>(null);
  readonly statusFailed = signal(false);
  readonly starting = signal(false);
  /** Why "Run now" was refused (409), shown beside the button. */
  readonly runMessage = signal<string | null>(null);
  private readonly started = signal<JobDto | null>(null);
  private readonly followedId = signal<string | null>(null);
  private readonly finishedIds = new Set<string>();
  /** The sweep being followed: the one "Run now" queued, else any active one (e.g. the scheduler's). */
  readonly job = computed(() => {
    const started = this.started();
    const id =
      this.followedId() ??
      started?.id ??
      this.jobs.activeJobs().find((j) => j.type === RETENTION_JOB_TYPE)?.id;
    if (!id) return null;
    const held = started?.id === id ? started : null;
    const live = this.jobs.job(id);
    return live ? newerJob(live, held) : held;
  });
  readonly running = computed(() => {
    const job = this.job();
    return !!job && isActiveJob(job);
  });
  readonly percent = computed(() => progressPercent(this.job()?.progress));

  /** Whether the API has the Apps Script block; an older one has none. */
  readonly rulesAvailable = signal(false);
  readonly rules = new FormArray<RuleForm>([]);
  readonly ruleErrors = signal<readonly string[]>([]);
  private readonly ruleEdits = new Subject<void>();
  readonly newRule = new FormGroup({
    label: new FormControl('', {
      nonNullable: true,
      validators: [scriptLabelValidator, this.uniqueRule()],
    }),
    days: new FormControl<number | null>(NEW_RULE_DAYS, ruleDaysValidators()),
  });
  readonly labels = signal<readonly string[] | null>(null);
  private readonly newLabel = toSignal(this.newRule.controls.label.valueChanges, {
    initialValue: '',
  });
  readonly labelOptions = computed(() => {
    const typed = this.newLabel().trim().toLowerCase();
    return (this.labels() ?? [])
      .filter((l) => l.toLowerCase().includes(typed))
      .slice(0, MAX_LABEL_OPTIONS);
  });

  constructor() {
    this.enabled.disable();
    this.enabled.valueChanges
      .pipe(
        concatMap((value) => this.saveEnabled(value)),
        takeUntilDestroyed(),
      )
      .subscribe();
    this.dayEdits
      .pipe(
        tap((type) => this.pendingTypes.add(type)),
        debounceTime(RETENTION_SAVE_DEBOUNCE_MS),
        concatMap(() => defer(() => this.saveDays())),
        takeUntilDestroyed(),
      )
      .subscribe();
    this.rules.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.ruleEdits.next());
    this.ruleEdits
      .pipe(
        debounceTime(RETENTION_SAVE_DEBOUNCE_MS),
        concatMap(() => defer(() => this.saveRules())),
        takeUntilDestroyed(),
      )
      .subscribe();
    effect(() => {
      const job = this.job();
      untracked(() => {
        if (!job) return;
        if (isActiveJob(job)) this.followedId.set(job.id);
        else if (!this.finishedIds.has(job.id)) this.finish(job);
      });
    });
    // The hub's snapshot holds active jobs only: an end missed while disconnected comes over REST.
    effect(() => {
      if (this.jobs.reconnects() === 0) return;
      const id = untracked(() => this.job()?.id);
      if (id)
        this.jobs
          .fetch(id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({ error: () => undefined });
    });
  }

  ngOnInit(): void {
    this.settingsApi
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the section stays disabled.
      .subscribe({ next: (settings) => this.load(settings), error: () => undefined });
    this.loadStatus();
  }

  loadStatus(): void {
    this.statusFailed.set(false);
    this.settingsApi
      .retentionStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the status offers a retry.
      .subscribe({ next: (s) => this.status.set(s), error: () => this.statusFailed.set(true) });
  }

  run(): void {
    if (!this.savedEnabled() || this.running() || this.starting()) return;
    this.starting.set(true);
    this.runMessage.set(null);
    this.settingsApi
      .runRetention()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (job) => {
          this.starting.set(false);
          this.started.set(job);
        },
        // A 409 is a state, not a failure: retention is off, or a sweep is already active.
        error: (error: unknown) => {
          this.starting.set(false);
          if (error instanceof HttpErrorResponse && error.status === 409) {
            this.runMessage.set(errorMessage(error));
            this.loadStatus();
          }
        },
      });
  }

  /** The server's message for a mail type's days, or null. */
  dayError(type: string): string | null {
    const control = this.days.controls[type];
    if (!control?.invalid) return null;
    return (
      (control.getError('server') as string | undefined) ??
      `${this.range.min} to ${this.range.max} whole days, or empty to keep.`
    );
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
  }

  removeRule(index: number): void {
    this.rules.removeAt(index);
    this.newRule.controls.label.updateValueAndValidity();
  }

  /** User labels for the suggestions, loaded on first focus. */
  loadLabels(): void {
    if (this.labels()) return;
    this.labels.set([]);
    this.labelsApi
      .labels()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (labels) =>
          this.labels.set(
            labels
              .filter((l) => l.type === 'user')
              .map((l) => l.name)
              .sort((a, b) => a.localeCompare(b)),
          ),
        // The error interceptor shows why; the label is typed instead.
        error: () => undefined,
      });
  }

  private load(settings: SettingsDto): void {
    const retention = settings.retention;
    if (retention) {
      const types = Object.keys(retention.days);
      for (const type of types) {
        const control = new FormControl<number | null>(retention.days[type], [
          Validators.min(RETENTION_DAYS.min),
          Validators.max(RETENTION_DAYS.max),
          wholeNumber,
        ]);
        control.valueChanges
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe(() => this.dayEdits.next(type));
        this.days.setControl(type, control, { emitEvent: false });
      }
      this.types.set(types);
      this.enabled.setValue(retention.enabled, { emitEvent: false });
      this.enabled.enable({ emitEvent: false });
      this.savedEnabled.set(retention.enabled);
    }
    const rules = settings.appsScript?.retentionRules;
    if (rules) {
      for (const rule of rules)
        this.rules.push(ruleForm(rule.label, rule.days), { emitEvent: false });
      this.rulesAvailable.set(true);
    }
    this.loaded.set(true);
  }

  private saveEnabled(enabled: boolean) {
    this.saving.update((n) => n + 1);
    return this.settingsApi.saveRetention({ retention: { enabled } }).pipe(
      map((settings) => {
        this.saving.update((n) => n - 1);
        this.savedEnabled.set(settings.retention?.enabled ?? enabled);
        this.runMessage.set(null);
        this.snackBar.open('Retention settings saved', undefined, { duration: 3000 });
        this.loadStatus();
      }),
      // The error interceptor shows why; the toggle goes back to the saved value.
      catchError(() => {
        this.saving.update((n) => n - 1);
        this.enabled.setValue(this.savedEnabled(), { emitEvent: false });
        return of(undefined);
      }),
    );
  }

  /** The valid edited types, each with its value now; invalid ones wait for a fix. */
  private saveDays() {
    const days: Record<string, number | null> = {};
    for (const type of [...this.pendingTypes]) {
      const control = this.days.controls[type];
      if (control.invalid) continue;
      days[type] = control.value;
      this.pendingTypes.delete(type);
    }
    if (!Object.keys(days).length) return of(undefined);
    this.saving.update((n) => n + 1);
    return this.settingsApi.saveRetention({ retention: { days } }).pipe(
      map(() => {
        this.saving.update((n) => n - 1);
        this.snackBar.open('Retention settings saved', undefined, { duration: 3000 });
        this.loadStatus();
      }),
      // A 400 is shown on its field; the error interceptor shows anything else.
      catchError((error: unknown) => {
        this.saving.update((n) => n - 1);
        for (const [field, messages] of Object.entries(problemErrors(error))) {
          const control = this.days.controls[field.replace(/^retention\.days\./, '')];
          if (!control || !field.startsWith('retention.days.')) continue;
          control.setErrors({ server: messages[0] });
          control.markAsTouched();
        }
        return of(undefined);
      }),
    );
  }

  /** Saves the whole Apps Script block (the API replaces it) with the latest saved other fields. */
  private saveRules() {
    if (this.rules.invalid) return of(undefined);
    const retentionRules = this.rules
      .getRawValue()
      .map((r) => ({ label: r.label.trim(), days: r.days ?? 0 }));
    this.saving.update((n) => n + 1);
    return this.settingsApi.getSettings().pipe(
      take(1),
      concatMap((settings) =>
        this.settingsApi.saveAppsScript({
          appsScript: { ...settings.appsScript!, retentionRules },
        }),
      ),
      map(() => {
        this.saving.update((n) => n - 1);
        this.ruleErrors.set([]);
        this.snackBar.open('Apps Script retention rules saved', undefined, { duration: 3000 });
        this.appsScriptSaved.emit();
      }),
      // A 400 is listed under the table; the error interceptor shows anything else.
      catchError((error: unknown) => {
        this.saving.update((n) => n - 1);
        this.ruleErrors.set(
          Object.entries(problemErrors(error)).flatMap(([field, messages]) =>
            messages.map((m) => `${ruleFieldName(field)}${m}`),
          ),
        );
        return of(undefined);
      }),
    );
  }

  private finish(job: JobDto): void {
    this.finishedIds.add(job.id);
    this.followedId.set(null);
    this.started.set(null);
    this.loadStatus();
    const message =
      job.status === 'completed'
        ? (job.progress?.message ?? 'Retention sweep finished.')
        : job.status === 'cancelled'
          ? 'Retention sweep cancelled.'
          : `Retention sweep failed${job.error ? `: ${job.error}` : '.'}`;
    this.snackBar.open(message, undefined, { duration: 6000 });
  }

  /** `{ duplicate: true }` when the label already has a retention rule. */
  private uniqueRule(): ValidatorFn {
    return (control: AbstractControl<string>) => {
      const key = scriptLabelKey(control.value ?? '');
      const taken = this.rules?.value.some((r) => scriptLabelKey(r.label ?? '') === key);
      return key && taken ? { duplicate: true } : null;
    };
  }
}

function ruleForm(label: string, days: number | null): RuleForm {
  return new FormGroup({
    label: new FormControl(label, { nonNullable: true }),
    days: new FormControl(days, ruleDaysValidators()),
  });
}

function ruleDaysValidators(): ValidatorFn[] {
  return [
    Validators.required,
    Validators.min(ARCHIVE_RULE_DAYS.min),
    Validators.max(ARCHIVE_RULE_DAYS.max),
    wholeNumber,
  ];
}

/** `scriptLabelError` as a validator: `{ scriptLabel: message }`. */
function scriptLabelValidator(control: AbstractControl<string>): ValidationErrors | null {
  const error = scriptLabelError(control.value ?? '');
  return error ? { scriptLabel: error } : null;
}

/** `appsScript.retentionRules[1].label` → "Rule 2 label: ". */
function ruleFieldName(field: string): string {
  const rule = /^appsScript\.retentionRules\[(\d+)\](?:\.(label|days))?$/.exec(field);
  return rule ? `Rule ${Number(rule[1]) + 1}${rule[2] ? ` ${rule[2]}` : ''}: ` : '';
}
