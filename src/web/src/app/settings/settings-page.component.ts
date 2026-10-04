import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { PageHeader } from '../layout/page-header';
import { GoogleClientSettings, SetupService } from '../setup/setup.service';
import { ConnectGmailStep } from '../setup/steps/connect-gmail-step.component';
import { GoogleClientStep } from '../setup/steps/google-client-step.component';
import { ModelsStep } from '../setup/steps/models-step.component';
import { OllamaUrlStep } from '../setup/steps/ollama-url-step.component';
import { AnalysisSettingsSection } from './analysis-settings.component';
import { AttachmentSettingsSection } from './attachment-settings.component';
import { ClaudeSettingsSection } from './claude-settings.component';
import { DataSettingsSection } from './data-settings.component';
import { LabelsSettingsSection } from './labels-settings.component';
import { ProtectionSettingsSection } from './protection-settings.component';
import {
  AnalysisSettings,
  AnalysisSettingsUpdate,
  AttachmentsSettingsDto,
  AttachmentsUpdate,
  ClaudeSettings,
  ClaudeSettingsDto,
  ClaudeSettingsUpdate,
  LabelSettings,
  PromptTemplateDto,
  ProtectionSettings,
} from './settings.models';
import { SettingsService } from './settings.service';

/** Same bounds as the API's `fetchChunkSize` validation; `step` only sets the arrow-key increment. */
export const FETCH_CHUNK = { min: 10, max: 5000, step: 10 } as const;

/**
 * `/settings` (M1): the wizard's step components, one section each. Every step saves only its own
 * fields (the API leaves missing fields unchanged), so saving one section never resets another.
 */
@Component({
  selector: 'app-settings-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    PageHeader,
    GoogleClientStep,
    ConnectGmailStep,
    OllamaUrlStep,
    ModelsStep,
    AnalysisSettingsSection,
    AttachmentSettingsSection,
    ClaudeSettingsSection,
    ProtectionSettingsSection,
    LabelsSettingsSection,
    DataSettingsSection,
  ],
  templateUrl: './settings-page.component.html',
  styles: `
    .section-title {
      font: var(--mat-sys-title-medium);
      margin: 0;
    }
    .section-hint {
      font: var(--mat-sys-body-medium);
      color: var(--mat-sys-on-surface-variant);
      margin: 0.25rem 0 1rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage implements OnInit {
  private readonly setup = inject(SetupService);
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly chunk = FETCH_CHUNK;
  readonly fetchLoaded = signal(false);
  readonly fetchSaving = signal(false);
  readonly fetchForm = new FormGroup({
    fetchChunkSize: new FormControl<number | null>(null, {
      validators: [
        Validators.required,
        Validators.min(FETCH_CHUNK.min),
        Validators.max(FETCH_CHUNK.max),
        wholeNumber,
      ],
    }),
  });
  readonly fetchChunkSize = this.fetchForm.controls.fetchChunkSize;

  /** The Google section reports a saved client ID and secret; the Gmail section enables Connect from it. */
  readonly clientSaved = signal(false);
  /** `null` until the Ollama URL is saved here: the models section lists against the saved URL. */
  readonly ollamaUrl = signal<string | null>(null);

  /** `null` until loaded; the labels section saves itself. */
  readonly labels = signal<LabelSettings | null>(null);

  readonly analysis = signal<AnalysisSettings | null>(null);
  /** The saved embedding model; the analysis cluster distance needs one. */
  readonly embeddingModel = signal<string | null>(null);
  readonly analysisSaving = signal(false);
  readonly analysisErrors = signal<Record<string, string[]> | null>(null);
  readonly defaultPrompt = signal<PromptTemplateDto | null>(null);

  /** `null` until loaded; an older API without the `attachments` block hides the section. */
  readonly attachments = signal<AttachmentsSettingsDto | null>(null);
  readonly attachmentsSaving = signal(false);
  readonly attachmentsErrors = signal<Record<string, string[]> | null>(null);

  /** `null` until loaded, and on an older API without Claude review, which hides the section. */
  readonly claude = signal<ClaudeSettings | null>(null);
  readonly claudeSaving = signal(false);
  readonly claudeErrors = signal<Record<string, string[]> | null>(null);

  /** `null` until loaded, and on an older API without protection rules, which hides the section. */
  readonly protection = signal<ProtectionSettings | null>(null);

  ngOnInit(): void {
    this.settingsApi
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.fetchChunkSize.reset(settings.fetchChunkSize);
          this.fetchLoaded.set(true);
          this.embeddingModel.set(settings.embeddingModel);
          this.labels.set({
            actionLabelName: settings.actionLabelName,
            deleteLabelName: settings.deleteLabelName,
          });
          this.analysis.set(settings);
          this.attachments.set(attachmentsOf(settings));
          this.claude.set(claudeOf(settings));
          this.protection.set(settings.protection ?? null);
        },
        // The error interceptor shows why; the field stays disabled without a saved value.
        error: () => undefined,
      });
    this.loadDefaultPrompt();
  }

  onClientChange(client: GoogleClientSettings): void {
    this.clientSaved.set(!!client.clientId && client.secretSet);
  }

  saveAnalysis(changes: AnalysisSettingsUpdate): void {
    if (this.analysisSaving()) return;
    if (Object.keys(changes).length === 0) {
      this.snackBar.open('No analysis changes to save', undefined, { duration: 3000 });
      return;
    }
    this.analysisSaving.set(true);
    this.analysisErrors.set(null);
    this.settingsApi
      .saveAnalysis(changes)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.analysisSaving.set(false);
          this.embeddingModel.set(settings.embeddingModel);
          this.analysis.set(settings);
          this.snackBar.open('Analysis settings saved', undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.analysisSaving.set(false);
          this.analysisErrors.set(validationErrors(error));
        },
      });
  }

  saveAttachments(changes: AttachmentsUpdate): void {
    if (this.attachmentsSaving()) return;
    if (Object.keys(changes).length === 0) {
      this.snackBar.open('No attachment changes to save', undefined, { duration: 3000 });
      return;
    }
    this.attachmentsSaving.set(true);
    this.attachmentsErrors.set(null);
    this.settingsApi
      .saveAttachments(changes)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.attachmentsSaving.set(false);
          this.attachments.set(attachmentsOf(settings));
          this.snackBar.open('Attachment settings saved', undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.attachmentsSaving.set(false);
          this.attachmentsErrors.set(validationErrors(error));
        },
      });
  }

  saveClaude(changes: ClaudeSettingsUpdate): void {
    if (this.claudeSaving()) return;
    if (Object.keys(changes).length === 0) {
      this.snackBar.open('No Claude review changes to save', undefined, { duration: 3000 });
      return;
    }
    this.claudeSaving.set(true);
    this.claudeErrors.set(null);
    this.settingsApi
      .saveClaude(changes)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.claudeSaving.set(false);
          this.claude.set(claudeOf(settings));
          this.snackBar.open('Claude review settings saved', undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.claudeSaving.set(false);
          this.claudeErrors.set(validationErrors(error));
        },
      });
  }

  /** Loads the built-in prompt once; the section previews it while no override is set. */
  loadDefaultPrompt(): void {
    if (this.defaultPrompt()) return;
    this.settingsApi
      .getDefaultPrompt()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the textarea keeps its text.
      .subscribe({ next: (prompt) => this.defaultPrompt.set(prompt), error: () => undefined });
  }

  saveFetch(): void {
    if (this.fetchSaving()) return;
    if (this.fetchForm.invalid) {
      this.fetchChunkSize.markAsTouched();
      return;
    }
    this.fetchSaving.set(true);
    this.setup
      .saveSettings({ fetchChunkSize: this.fetchChunkSize.value ?? undefined })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.fetchSaving.set(false);
          this.fetchChunkSize.reset(settings.fetchChunkSize);
          this.snackBar.open('Fetch chunk size saved', undefined, { duration: 3000 });
        },
        error: () => this.fetchSaving.set(false),
      });
  }
}

/** `min`/`max` accept 150.5; the API's integer field would reject it with a bare 400. */
function wholeNumber(control: AbstractControl<number | null>): ValidationErrors | null {
  const value = control.value;
  return value === null || Number.isInteger(value) ? null : { integer: true };
}

function attachmentsOf(settings: AttachmentsSettingsDto): AttachmentsSettingsDto {
  return { attachments: settings.attachments, visionModel: settings.visionModel };
}

function claudeOf(settings: ClaudeSettingsDto): ClaudeSettings | null {
  return settings.claudeReviewerMode === undefined ? null : (settings as ClaudeSettings);
}

/** ValidationProblem `errors` from a 400, keyed by API field name. */
function validationErrors(error: unknown): Record<string, string[]> | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return null;
  return (error.error as { errors?: Record<string, string[]> } | null)?.errors ?? null;
}
