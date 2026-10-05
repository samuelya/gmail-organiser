import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSliderModule } from '@angular/material/slider';
import {
  ANALYSIS_LIMITS,
  AnalysisGroupingMode,
  AnalysisSettings,
  AnalysisSettingsUpdate,
  EMAILS_PLACEHOLDER,
  GROUPING_MODES,
  MAX_PROMPT_TEMPLATE_LENGTH,
  NumericAnalysisField,
  PROMPT_PLACEHOLDERS,
  PromptTemplateDto,
  Range,
} from './settings.models';

type NumberControl = FormControl<number | null>;

interface NumberField {
  key: NumericAnalysisField;
  label: string;
  hint: string;
}

/**
 * The Settings page's "Analysis" section. Dumb: settings in, the changed fields out on Save; the
 * page owns the API calls and passes back server field errors and the default prompt.
 */
@Component({
  selector: 'app-analysis-settings',
  imports: [
    NgTemplateOutlet,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatRadioModule,
    MatSlideToggleModule,
    MatSliderModule,
  ],
  templateUrl: './analysis-settings.component.html',
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .prompt {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    code {
      font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnalysisSettingsSection {
  /** `null` until loaded; the form stays disabled until then. */
  readonly settings = input<AnalysisSettings | null>(null);
  /** The cluster distance only applies with an embedding model. */
  readonly embeddingModel = input<string | null>(null);
  readonly saving = input(false);
  /** ValidationProblem `errors` from the last save, keyed by API field name. */
  readonly serverErrors = input<Record<string, string[]> | null>(null);
  /** The built-in prompt, pre-filled while no override is saved (the page loads it on init, or again on `resetPrompt` if that failed). */
  readonly defaultPrompt = input<PromptTemplateDto | null>(null);

  readonly changed = output<AnalysisSettingsUpdate>();
  readonly resetPrompt = output<void>();

  readonly limits = ANALYSIS_LIMITS;
  readonly groupingModes = GROUPING_MODES;
  readonly placeholders = PROMPT_PLACEHOLDERS;
  readonly emailsPlaceholder = EMAILS_PLACEHOLDER;
  readonly maxPromptLength = MAX_PROMPT_TEMPLATE_LENGTH;
  readonly batchFields: readonly NumberField[] = [
    {
      key: 'analysisDefaultCount',
      label: 'Default analysis count',
      hint: 'Emails a new analysis batch takes unless you choose another number.',
    },
    {
      key: 'analysisBodyMaxChars',
      label: 'Body max characters',
      hint: 'Longer email bodies are cut to this length before they reach the LLM.',
    },
  ];
  readonly groupFields: readonly NumberField[] = [
    {
      key: 'analysisRepresentativesPerGroup',
      label: 'Representatives per group',
      hint: 'Emails from each group the LLM reads.',
    },
    {
      key: 'analysisMinGroupSize',
      label: 'Minimum group size',
      hint: 'Smaller groups are analysed email by email.',
    },
    {
      key: 'analysisDerivedConfidencePenalty',
      label: 'Derived confidence penalty',
      hint: 'Subtracted from the confidence of suggestions copied to the rest of a group.',
    },
  ];
  readonly clusterField = computed<NumberField>(() => ({
    key: 'analysisClusterDistance',
    label: 'Cluster distance',
    hint: this.embeddingModel()
      ? 'Maximum embedding distance within one Auto group; lower is stricter.'
      : 'Choose an embedding model in the Ollama section to use this.',
  }));
  readonly taxonomyFields: readonly NumberField[] = [
    {
      key: 'taxonomyMaxSenders',
      label: 'Senders to profile',
      hint: 'Top senders by message count that "Propose taxonomy" sends to the LLM.',
    },
    {
      key: 'taxonomyMaxLabels',
      label: 'Maximum labels',
      hint: 'Most area labels one taxonomy proposal may suggest.',
    },
  ];
  readonly contextField: NumberField = {
    key: 'llmNumCtx',
    label: 'LLM context (tokens)',
    hint: 'The context window each LLM call asks for. Raise it when a run shows "near context limit".',
  };
  readonly memoryField: NumberField = {
    key: 'analysisMemoryMinApprovals',
    label: 'Minimum approvals',
    hint: 'Matching approved decisions needed before the LLM is skipped.',
  };

  readonly form = new FormGroup({
    analysisDefaultCount: numberControl('analysisDefaultCount'),
    analysisBodyMaxChars: numberControl('analysisBodyMaxChars'),
    analysisGroupingMode: new FormControl<AnalysisGroupingMode>('auto', { nonNullable: true }),
    analysisRepresentativesPerGroup: numberControl('analysisRepresentativesPerGroup'),
    analysisMinGroupSize: numberControl('analysisMinGroupSize'),
    analysisDerivedConfidencePenalty: numberControl('analysisDerivedConfidencePenalty'),
    analysisClusterDistance: numberControl('analysisClusterDistance'),
    analysisMemoryShortCircuit: new FormControl(false, { nonNullable: true }),
    analysisMemoryMinApprovals: numberControl('analysisMemoryMinApprovals'),
    bulkApproveThreshold: numberControl('bulkApproveThreshold'),
    autoArchiveOnActionDone: new FormControl(false, { nonNullable: true }),
    policyAutoApplyFetched: new FormControl(false, { nonNullable: true }),
    llmNumCtx: numberControl('llmNumCtx'),
    taxonomyMaxSenders: numberControl('taxonomyMaxSenders'),
    taxonomyMaxLabels: numberControl('taxonomyMaxLabels'),
    analysisPromptTemplate: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MAX_PROMPT_TEMPLATE_LENGTH), requiresEmailsPlaceholder],
    }),
  });
  readonly c = this.form.controls;

  private readonly promptText = toSignal(this.c.analysisPromptTemplate.valueChanges, {
    initialValue: '',
  });
  /** Which prompt the field text amounts to; `unknown` while the built-in prompt is not loaded. */
  readonly promptSource = computed<'built-in' | 'custom' | 'unknown'>(() => {
    const builtIn = this.defaultPrompt();
    if (!builtIn) return 'unknown';
    const text = this.promptText().trim();
    return text === '' || text === builtIn.template.trim() ? 'built-in' : 'custom';
  });
  /** Reset was pressed before the built-in prompt loaded: fill it in when it arrives. */
  private pendingReset = false;

  constructor() {
    this.form.disable();
    effect(() => {
      const settings = this.settings();
      untracked(() => this.load(settings));
    });
    // A model chosen in the Ollama section enables the field without resetting other edits.
    effect(() => {
      this.embeddingModel();
      untracked(() => this.applyClusterState());
    });
    effect(() => {
      const builtIn = this.defaultPrompt();
      untracked(() => this.fillDefaultPrompt(builtIn));
    });
    effect(() => {
      const errors = this.serverErrors();
      untracked(() => this.showServerErrors(errors));
    });
  }

  /** Puts the built-in text back; the default text is never saved as custom, so saving stores no override. */
  resetToDefault(): void {
    const control = this.c.analysisPromptTemplate;
    const builtIn = this.defaultPrompt();
    control.setValue(builtIn?.template ?? '');
    control.markAsDirty();
    if (!builtIn) {
      this.pendingReset = true;
      this.resetPrompt.emit();
    }
  }

  save(): void {
    if (this.saving() || !this.settings()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.changed.emit(this.changes());
  }

  /** The fields that differ from the loaded settings; disabled controls are never sent. */
  changes(): AnalysisSettingsUpdate {
    const saved = this.settings();
    if (!saved) return {};
    const changes: Record<string, unknown> = {};
    for (const [key, control] of Object.entries(this.c) as [
      keyof AnalysisSettings,
      AbstractControl,
    ][]) {
      if (control.disabled) continue;
      const value: unknown = control.value;
      if (key === 'analysisPromptTemplate') {
        let text = (value as string).trim();
        // A copy of the built-in prompt is not an override: send empty so the API stores null.
        if (text === this.defaultPrompt()?.template.trim()) text = '';
        if (text !== (saved.analysisPromptTemplate ?? '').trim()) changes[key] = text;
      } else if (value !== saved[key]) {
        changes[key] = value;
      }
    }
    return changes as AnalysisSettingsUpdate;
  }

  control(key: NumericAnalysisField): NumberControl {
    return this.c[key];
  }

  range(key: NumericAnalysisField): Range {
    return ANALYSIS_LIMITS[key];
  }

  formatThreshold(value: number): string {
    return value.toFixed(2);
  }

  private load(settings: AnalysisSettings | null): void {
    if (!settings) {
      this.form.disable();
      return;
    }
    this.form.enable();
    this.pendingReset = false;
    const prompt = settings.analysisPromptTemplate?.trim() ? settings.analysisPromptTemplate : '';
    this.form.reset({ ...settings, analysisPromptTemplate: prompt });
    this.fillDefaultPrompt(this.defaultPrompt());
    this.applyClusterState();
  }

  /** Shows the built-in text in an untouched field with no override, or after a pending Reset; never overwrites edits. */
  private fillDefaultPrompt(builtIn: PromptTemplateDto | null): void {
    const settings = this.settings();
    const control = this.c.analysisPromptTemplate;
    if (!builtIn || !settings || control.value !== '') return;
    if (this.pendingReset) {
      this.pendingReset = false;
      control.setValue(builtIn.template);
      control.markAsDirty();
    } else if (control.pristine && !settings.analysisPromptTemplate?.trim()) {
      control.setValue(builtIn.template);
      control.markAsPristine();
    }
  }

  private applyClusterState(): void {
    const control = this.c.analysisClusterDistance;
    if (this.settings() && this.embeddingModel()) control.enable();
    else control.disable();
  }

  private showServerErrors(errors: Record<string, string[]> | null): void {
    for (const [key, messages] of Object.entries(errors ?? {})) {
      const control = this.form.get(key);
      if (control && messages.length > 0) {
        control.setErrors({ server: messages[0] });
        control.markAsTouched();
      }
    }
  }
}

function numberControl(field: NumericAnalysisField): NumberControl {
  const { min, max, integer } = ANALYSIS_LIMITS[field];
  const validators: ValidatorFn[] = [Validators.required, Validators.min(min), Validators.max(max)];
  if (integer) validators.push(wholeNumber);
  return new FormControl<number | null>(null, { validators });
}

/** `min`/`max` accept 2.5; the API's integer fields would reject it. */
function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  return value === null || Number.isInteger(value) ? null : { integer: true };
}

/** The API rejects a custom prompt without `{{emails}}`; an empty one restores the built-in prompt. */
function requiresEmailsPlaceholder(control: AbstractControl<string>): ValidationErrors | null {
  const value = control.value.trim();
  return value === '' || value.includes(EMAILS_PLACEHOLDER) ? null : { emailsPlaceholder: true };
}
