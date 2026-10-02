import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of, switchMap } from 'rxjs';
import { LlmModels, LlmService, OllamaModel } from '../core/llm.service';
import { ModelOption, modelOptions } from '../setup/steps/models-step.component';
import {
  ARCHIVE_TYPE,
  ATTACHMENT_LIMITS,
  ATTACHMENT_MB_FIELDS,
  ATTACHMENT_TYPES,
  AttachmentImageMode,
  AttachmentSettings,
  AttachmentSettingsUpdate,
  AttachmentsUpdate,
  AttachmentType,
  AttachmentTypeSetting,
  BYTES_PER_MB,
  IMAGE_MODES,
  NumericAttachmentField,
  Range,
  VISION_CAPABILITY,
} from './settings.models';

type NumberControl = FormControl<number | null>;

interface NumberField {
  key: NumericAttachmentField;
  label: string;
  hint: string;
}

const TYPE_NAMES = Object.keys(ATTACHMENT_TYPES) as AttachmentType[];

/**
 * The Settings page's "Attachments" section. Dumb about saving: settings in, the changed fields
 * out on Save; the page owns the API call and passes back server field errors. Lists the Ollama
 * models itself, only while the vision model picker is shown.
 */
@Component({
  selector: 'app-attachment-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatRadioModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatTooltipModule,
  ],
  templateUrl: './attachment-settings.component.html',
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .warn {
      color: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AttachmentSettingsSection {
  private readonly llm = inject(LlmService);

  /** `null` until loaded; the form stays disabled until then. */
  readonly settings = input<AttachmentSettings | null>(null);
  /** The saved vision model; `undefined` when the API has no vision model setting. */
  readonly visionModel = input<string | null | undefined>(undefined);
  /** The Ollama URL the models are listed against; `null` uses the saved URL. */
  readonly baseUrl = input<string | null>(null);
  readonly saving = input(false);
  /** ValidationProblem `errors` from the last save, keyed by API field name. */
  readonly serverErrors = input<Record<string, string[]> | null>(null);

  readonly changed = output<AttachmentsUpdate>();

  readonly typeNames = TYPE_NAMES;
  readonly typeInfo = ATTACHMENT_TYPES;
  readonly archive = ARCHIVE_TYPE;
  readonly imageModes = IMAGE_MODES;
  readonly numberFields: readonly NumberField[] = [
    {
      key: 'maxBytes',
      label: 'Max attachment size (MB)',
      hint: 'Larger attachments are listed by name only.',
    },
    {
      key: 'maxImageBytes',
      label: 'Max image size (MB)',
      hint: 'Larger images are not read.',
    },
    {
      key: 'maxChars',
      label: 'Max text per attachment (characters)',
      hint: 'Longer text is cut to this length before it reaches the LLM.',
    },
    {
      key: 'maxPerMessage',
      label: 'Max attachments per message',
      hint: 'Further attachments are listed by name only.',
    },
  ];

  readonly form = new FormGroup({
    enabled: new FormControl(true, { nonNullable: true }),
    types: new FormGroup(
      Object.fromEntries(
        TYPE_NAMES.map((t) => [t, new FormControl(false, { nonNullable: true })]),
      ) as Record<AttachmentType, FormControl<boolean>>,
    ),
    maxBytes: numberControl('maxBytes'),
    maxImageBytes: numberControl('maxImageBytes'),
    maxChars: numberControl('maxChars'),
    maxPerMessage: numberControl('maxPerMessage'),
    imageMode: new FormControl<AttachmentImageMode>('ocr', { nonNullable: true }),
    visionModel: new FormControl<string | null>(null),
  });
  readonly c = this.form.controls;
  /** Server errors that belong to no single field (e.g. a type entry). */
  readonly otherErrors = signal<string[]>([]);

  /** The image-reading choice exists only on an API that returns `imageMode`. */
  readonly imageModeSupported = computed(() => this.settings()?.imageMode !== undefined);
  private readonly imageMode = toSignal(this.c.imageMode.valueChanges, { initialValue: 'ocr' });
  readonly visionShown = computed(
    () =>
      this.imageModeSupported() &&
      this.visionModel() !== undefined &&
      this.imageMode() === 'vision',
  );
  readonly models = signal<LlmModels | null>(null);
  readonly modelsLoading = signal(false);
  readonly vision = computed(() =>
    visionChoices(this.models()?.chatModels ?? [], this.visionModel()),
  );

  constructor() {
    this.form.disable();
    effect(() => {
      const settings = this.settings();
      this.visionModel();
      untracked(() => this.load(settings));
    });
    effect(() => {
      const errors = this.serverErrors();
      untracked(() => this.showServerErrors(errors));
    });
    toObservable(computed(() => (this.visionShown() ? { url: this.baseUrl() } : null)))
      .pipe(
        switchMap((request) => {
          if (!request) return of(null);
          this.modelsLoading.set(true);
          return this.llm.getModels(request.url).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((models) => {
        this.modelsLoading.set(false);
        this.models.set(models);
      });
    this.c.imageMode.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.applyVision());
  }

  save(): void {
    if (this.saving() || !this.settings()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.changed.emit(this.changes());
  }

  /** Puts the form back to the loaded settings. */
  discard(): void {
    this.load(this.settings());
  }

  /** The fields that differ from the loaded settings; disabled controls are never sent. */
  changes(): AttachmentsUpdate {
    const saved = this.settings();
    if (!saved) return {};
    const attachments: AttachmentSettingsUpdate = {};
    if (this.c.enabled.value !== saved.enabled) attachments.enabled = this.c.enabled.value;

    const types: AttachmentTypeSetting[] = [];
    for (const type of TYPE_NAMES) {
      const control = this.c.types.controls[type];
      if (control.enabled && control.value !== savedTypeEnabled(saved, type)) {
        types.push({ type, enabled: control.value });
      }
    }
    if (types.length > 0) attachments.types = types;

    for (const { key } of this.numberFields) {
      const value = this.c[key].value;
      if (value === null || value === toForm(key, saved[key])) continue;
      attachments[key] = toApi(key, value);
    }
    if (this.c.imageMode.enabled && this.c.imageMode.value !== saved.imageMode) {
      attachments.imageMode = this.c.imageMode.value;
    }

    const changes: AttachmentsUpdate = {};
    if (Object.keys(attachments).length > 0) changes.attachments = attachments;
    const model = this.c.visionModel;
    // The API treats a missing value as "unchanged" and an empty name as "clear".
    if (model.enabled && (model.value ?? '') !== (this.visionModel() ?? '')) {
      changes.visionModel = model.value ?? '';
    }
    return changes;
  }

  control(key: NumericAttachmentField): NumberControl {
    return this.c[key];
  }

  range(key: NumericAttachmentField): Range {
    return ATTACHMENT_LIMITS[key];
  }

  rangeMessage(key: NumericAttachmentField): string {
    const { min, max } = ATTACHMENT_LIMITS[key];
    return ATTACHMENT_MB_FIELDS.includes(key)
      ? `Enter a size from 64 KB (${min} MB) to ${max} MB.`
      : `Enter a number from ${min} to ${max}.`;
  }

  private load(settings: AttachmentSettings | null): void {
    this.otherErrors.set([]);
    if (!settings) {
      this.form.disable();
      return;
    }
    this.form.enable();
    this.form.reset({
      enabled: settings.enabled,
      types: Object.fromEntries(TYPE_NAMES.map((t) => [t, savedTypeEnabled(settings, t)])),
      maxBytes: toForm('maxBytes', settings.maxBytes),
      maxImageBytes: toForm('maxImageBytes', settings.maxImageBytes),
      maxChars: settings.maxChars,
      maxPerMessage: settings.maxPerMessage,
      imageMode: settings.imageMode ?? 'ocr',
      visionModel: this.visionModel() ?? null,
    });
    // Archives are never opened: the API rejects enabling them.
    this.c.types.controls[ARCHIVE_TYPE].setValue(false);
    this.c.types.controls[ARCHIVE_TYPE].disable();
    if (settings.imageMode === undefined) this.c.imageMode.disable();
    this.applyVision();
  }

  private applyVision(): void {
    const control = this.c.visionModel;
    if (this.settings() && this.visionShown()) control.enable({ emitEvent: false });
    else control.disable({ emitEvent: false });
  }

  private showServerErrors(errors: Record<string, string[]> | null): void {
    const other: string[] = [];
    for (const [key, messages] of Object.entries(errors ?? {})) {
      if (messages.length === 0) continue;
      const control = this.fieldFor(key);
      if (control) {
        control.setErrors({ server: messages[0] });
        control.markAsTouched();
      } else if (key.startsWith('attachments.') || key === 'visionModel') {
        other.push(messages[0]);
      }
    }
    this.otherErrors.set(other);
  }

  private fieldFor(key: string): AbstractControl | null {
    if (key === 'visionModel') return this.c.visionModel;
    const field = key.replace(/^attachments\./, '');
    return field !== key && field in ATTACHMENT_LIMITS ? this.form.get(field) : null;
  }
}

/**
 * The vision picker's options: vision-capable models when Ollama reports capabilities, otherwise all.
 * Like the API, a model with an empty capability list is unknown and stays listed (`unknown`).
 */
export function visionChoices(
  models: OllamaModel[],
  saved: string | null | undefined,
): { options: ModelOption[]; filtered: boolean; unknown: boolean } {
  const filtered = models.some((m) => m.capabilities.length > 0);
  if (!filtered) return { options: modelOptions(models, saved), filtered, unknown: false };
  const list = models.filter(
    (m) => m.capabilities.length === 0 || m.capabilities.includes(VISION_CAPABILITY),
  );
  const options = modelOptions(list, null).map((o, i) =>
    list[i].capabilities.length === 0 ? { ...o, label: `${o.label} (capabilities unknown)` } : o,
  );
  if (saved && !list.some((m) => m.name === saved)) {
    const onServer = models.some((m) => m.name === saved);
    const note = onServer ? 'no vision capability reported' : 'not found on the server';
    options.unshift({ value: saved, label: `${saved} (${note})` });
  }
  return { options, filtered, unknown: list.some((m) => m.capabilities.length === 0) };
}

function savedTypeEnabled(settings: AttachmentSettings, type: AttachmentType): boolean {
  return type !== ARCHIVE_TYPE && !!settings.types.find((t) => t.type === type)?.enabled;
}

/** Bytes to MB (four decimals, so 64 KB stays 0.0625) for the size fields; other fields as is. */
function toForm(key: NumericAttachmentField, value: number): number {
  return ATTACHMENT_MB_FIELDS.includes(key)
    ? Math.round((value / BYTES_PER_MB) * 10_000) / 10_000
    : value;
}

function toApi(key: NumericAttachmentField, value: number): number {
  return ATTACHMENT_MB_FIELDS.includes(key) ? Math.round(value * BYTES_PER_MB) : value;
}

function numberControl(field: NumericAttachmentField): NumberControl {
  const { min, max, integer } = ATTACHMENT_LIMITS[field];
  const validators: ValidatorFn[] = [Validators.required, Validators.min(min), Validators.max(max)];
  if (integer) validators.push(wholeNumber);
  return new FormControl<number | null>(null, { validators });
}

/** `min`/`max` accept 2.5; the API's integer fields would reject it. */
function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  return value === null || Number.isInteger(value) ? null : { integer: true };
}
