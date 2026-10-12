import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  Injector,
  OnInit,
  signal,
  viewChild,
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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { VersionService } from '../core/version.service';
import { PageHeader } from '../layout/page-header';
import { GoogleClientSettings, SetupService } from '../setup/setup.service';
import {
  ConnectGmailStep,
  ConnectResult,
  parseConnectResult,
} from '../setup/steps/connect-gmail-step.component';
import { GoogleClientStep } from '../setup/steps/google-client-step.component';
import { ModelsStep } from '../setup/steps/models-step.component';
import { OllamaUrlStep } from '../setup/steps/ollama-url-step.component';
import { AnalysisSettingsSection } from './analysis-settings.component';
import { AttachmentSettingsSection } from './attachment-settings.component';
import { ClaudeSettingsSection } from './claude-settings.component';
import { DataSettingsSection } from './data-settings.component';
import { LabelsSettingsSection } from './labels-settings.component';
import { LlmProviderSettingsSection } from './llm-provider-settings.component';
import { PackSettingsSection } from './pack-settings.component';
import { ProtectionSettingsSection } from './protection-settings.component';
import { AppsScriptSettingsSection } from './apps-script-settings.component';
import { RetentionSettingsSection } from './retention-settings.component';
import { TaxonomySettingsSection } from './taxonomy-settings.component';
import { TriageModelSettingsSection } from './triage-model-settings.component';
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
  SettingsDto,
} from './settings.models';
import { SettingsService } from './settings.service';

/** Same bounds as the API's `fetchChunkSize` validation; `step` only sets the arrow-key increment. */
export const FETCH_CHUNK = { min: 10, max: 5000, step: 10 } as const;

/** The cards in page order; `id` is the heading's `settings-<id>` and the `?section=` value. */
export const SECTIONS = [
  { id: 'setup', label: 'Setup' },
  { id: 'google', label: 'Google' },
  { id: 'gmail', label: 'Gmail connection' },
  { id: 'llm-provider', label: 'Analysis model provider' },
  { id: 'ollama', label: 'Ollama' },
  { id: 'fetch', label: 'Fetch' },
  { id: 'labels', label: 'Labels' },
  { id: 'analysis', label: 'Analysis' },
  { id: 'taxonomy', label: 'Taxonomy' },
  { id: 'attachments', label: 'Attachments' },
  { id: 'protection', label: 'Protection' },
  { id: 'apps-script', label: 'Apps Script' },
  { id: 'retention', label: 'Retention' },
  { id: 'claude', label: 'Claude review' },
  { id: 'data', label: 'Data' },
] as const;

export type SettingsSection = (typeof SECTIONS)[number];
export type SettingsSectionId = SettingsSection['id'];

/** Each card's heading text, by section id. */
const SECTION_LABEL = Object.fromEntries(SECTIONS.map((s) => [s.id, s.label])) as Record<
  SettingsSectionId,
  string
>;

/**
 * How long the deep link keeps its heading in view while the cards above load (the models list
 * waits on the LLM host), unless the user scrolls first.
 */
export const SECTION_FOLLOW_MS = 5000;
/** Any of these means the user took over the scroll, so the deep link stops following. */
const USER_SCROLL_EVENTS = ['wheel', 'touchstart', 'pointerdown', 'keydown'] as const;

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
    RouterLink,
    GoogleClientStep,
    ConnectGmailStep,
    OllamaUrlStep,
    ModelsStep,
    AnalysisSettingsSection,
    AttachmentSettingsSection,
    ClaudeSettingsSection,
    LlmProviderSettingsSection,
    ProtectionSettingsSection,
    AppsScriptSettingsSection,
    RetentionSettingsSection,
    TaxonomySettingsSection,
    TriageModelSettingsSection,
    PackSettingsSection,
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
    .index-title {
      font: var(--mat-sys-title-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0 0 0.25rem;
    }
    /* Short labels would centre in the button's minimum width and break the column's edge. */
    .settings-index button {
      min-width: 0;
    }
    .app-version {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      text-align: center;
      margin: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage implements OnInit {
  private readonly setup = inject(SetupService);
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly versionApi = inject(VersionService);
  private readonly injector = inject(Injector);

  /** Set when the OAuth callback redirected back here (`/settings?gmail=…`); read once. */
  readonly connectResult: ConnectResult | null;

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

  /** `null` until loaded; the provider section saves itself and reports each saved settings back. */
  readonly llmProvider = signal<SettingsDto | null>(null);

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

  /**
   * The optional cards' settings, `null` where the card is hidden (still loading, or an older API
   * without that block). The template's `@if`s and the index both read this.
   */
  readonly optionalCards = computed(() => ({
    attachments: this.attachments()?.attachments ?? null,
    protection: this.protection(),
    claude: this.claude(),
  }));

  /** The index lists only the cards on the page. */
  readonly sections = computed<readonly SettingsSection[]>(() => {
    const cards: Partial<Record<SettingsSectionId, unknown>> = this.optionalCards();
    return SECTIONS.filter((s) => !(s.id in cards) || cards[s.id] !== null);
  });

  readonly label = SECTION_LABEL;

  private readonly cards = viewChild.required<ElementRef<HTMLElement>>('cards');

  /** `?section=<id>` from a deep link; scrolled to once the settings have loaded. */
  private readonly initialSection: string | null;

  /** The api's version for the footer; `null` until loaded or when the api is unreachable. */
  readonly version = signal<string | null>(null);

  constructor() {
    const params = this.route.snapshot.queryParamMap;
    const section = params.get('section');
    this.initialSection = SECTIONS.some((s) => s.id === section) ? section : null;
    this.connectResult = parseConnectResult(params.get('gmail'), params.get('reason'));
    if (this.connectResult) {
      // Cleared so a refresh doesn't show the result again.
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: {},
        replaceUrl: true,
      });
      afterNextRender(() =>
        this.host.nativeElement
          .querySelector('[data-testid="section-gmail"]')
          ?.scrollIntoView?.({ block: 'start' }),
      );
    }
  }

  /**
   * Scrolls to a card from an in-page link and moves focus to its heading until it is left.
   * Smooth unless the user prefers reduced motion or the caller asks for `auto`.
   */
  jumpToSection(headingId: string, behavior?: ScrollBehavior): HTMLElement | null {
    const heading = this.host.nativeElement.querySelector<HTMLElement>(`#${headingId}`);
    if (!heading) return null;
    const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    heading.scrollIntoView?.({
      block: 'start',
      behavior: behavior ?? (reduceMotion ? 'auto' : 'smooth'),
    });
    heading.setAttribute('tabindex', '-1');
    heading.addEventListener('blur', () => heading.removeAttribute('tabindex'), { once: true });
    heading.focus({ preventScroll: true });
    return heading;
  }

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
            documentTypeParent: settings.documentTypeParent ?? null,
          });
          this.llmProvider.set(settings);
          this.analysis.set(settings);
          this.attachments.set(attachmentsOf(settings));
          this.claude.set(claudeOf(settings));
          this.protection.set(settings.protection ?? null);
          this.scrollToInitialSection();
        },
        // The error interceptor shows why; the field stays disabled without a saved value.
        error: () => this.scrollToInitialSection(),
      });
    this.loadDefaultPrompt();
    this.versionApi
      .getVersion()
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the footer stays hidden.
      .subscribe({ next: (version) => this.version.set(version), error: () => undefined });
  }

  /**
   * Waits for the load: the optional cards only render once their settings are in. Skipped after
   * the OAuth callback, whose result on the Gmail card is the thing to see.
   */
  private scrollToInitialSection(): void {
    const section = this.initialSection;
    if (!section || this.connectResult) return;
    afterNextRender(
      () => {
        const heading = this.jumpToSection(`settings-${section}`, 'auto');
        if (heading) this.followWhileCardsLoad(heading);
      },
      { injector: this.injector },
    );
  }

  /**
   * The cards above the target load their own data after the jump and grow, pushing the heading
   * down where the browser has no scroll anchoring. Re-scrolls on every change in the cards' height
   * for `SECTION_FOLLOW_MS`, or until the user scrolls, clicks or presses a key.
   */
  private followWhileCardsLoad(heading: HTMLElement): void {
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() =>
      heading.scrollIntoView?.({ block: 'start', behavior: 'auto' }),
    );
    const timer = setTimeout(stop, SECTION_FOLLOW_MS);
    const removeDestroy = this.destroyRef.onDestroy(() => stop());
    function stop() {
      observer.disconnect();
      clearTimeout(timer);
      for (const type of USER_SCROLL_EVENTS) window.removeEventListener(type, stop, true);
      removeDestroy();
    }
    for (const type of USER_SCROLL_EVENTS) {
      window.addEventListener(type, stop, { capture: true, passive: true });
    }
    observer.observe(this.cards().nativeElement);
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
