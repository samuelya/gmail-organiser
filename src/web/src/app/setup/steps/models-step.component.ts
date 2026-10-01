import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, of, Subscription, switchMap, tap } from 'rxjs';
import {
  LlmModels,
  LlmService,
  ModelKind,
  modelLabel,
  OllamaModel,
} from '../../core/llm.service';
import { SetupState } from '../setup-state';
import { AppSettings, SetupService } from '../setup.service';

/** Repository-relative path of the Ollama guide. */
export const OLLAMA_GUIDE = 'docs/setup/ollama.md';

export interface ModelOption {
  value: string;
  label: string;
}

export type ModelTest =
  | { state: 'idle' }
  | { state: 'running' }
  | { state: 'ok'; seconds: string }
  | { state: 'error'; error: string };

const IDLE: ModelTest = { state: 'idle' };

/**
 * Chat and embedding model pickers, filled from the Ollama capabilities, each with its own
 * cancellable "Test model". The embedding model is optional. Has no stepper code, so the
 * Settings page embeds it as is.
 */
@Component({
  selector: 'app-models-step',
  imports: [
    NgTemplateOutlet,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './models-step.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelsStep implements OnInit {
  private readonly setup = inject(SetupService);
  private readonly setupState = inject(SetupState);
  private readonly llm = inject(LlmService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly tests: Partial<Record<ModelKind, Subscription>> = {};

  /** The Ollama URL to list and test against; `null` uses the saved URL. */
  readonly baseUrl = input<string | null>(null);
  /** Emits after a successful save. */
  readonly saved = output<AppSettings>();

  readonly guide = OLLAMA_GUIDE;
  readonly models = signal<LlmModels | null>(null);
  readonly loading = signal(true);
  readonly saving = signal(false);
  private readonly settings = signal<AppSettings | null>(null);
  readonly chatTest = signal<ModelTest>(IDLE);
  readonly embeddingTest = signal<ModelTest>(IDLE);

  readonly form = new FormGroup({
    chatModel: new FormControl<string | null>(null, Validators.required),
    /** `null` is "None": memory falls back to the exact sender. */
    embeddingModel: new FormControl<string | null>(null),
  });

  readonly chatOptions = computed(() =>
    options(this.models()?.chatModels ?? [], this.settings()?.chatModel),
  );
  readonly embeddingOptions = computed(() =>
    options(this.models()?.embeddingModels ?? [], this.settings()?.embeddingModel),
  );
  readonly reachable = computed(() => this.models()?.reachable ?? false);
  readonly noChatModels = computed(() => this.reachable() && !this.models()?.chatModels.length);
  readonly noEmbeddingModels = computed(
    () => this.reachable() && !this.models()?.embeddingModels.length,
  );

  constructor() {
    this.destroyRef.onDestroy(() => Object.values(this.tests).forEach((s) => s?.unsubscribe()));
    toObservable(this.baseUrl)
      .pipe(
        tap(() => this.loading.set(true)),
        switchMap((url) => this.llm.getModels(url).pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((models) => {
        this.loading.set(false);
        this.models.set(models);
      });
    // A result belongs to the model it was run for.
    this.form.controls.chatModel.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.cancelTest('chat'));
    this.form.controls.embeddingModel.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.cancelTest('embedding'));
  }

  ngOnInit(): void {
    this.setup
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => this.apply(settings));
  }

  refresh(): void {
    this.loading.set(true);
    this.llm
      .getModels(this.baseUrl())
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((models) => {
        this.loading.set(false);
        this.models.set(models);
      });
  }

  testState(kind: ModelKind) {
    return kind === 'chat' ? this.chatTest : this.embeddingTest;
  }

  /** Runs in the background: the rest of the form stays usable, and Cancel aborts the request. */
  test(kind: ModelKind): void {
    const model = this.form.controls[kind === 'chat' ? 'chatModel' : 'embeddingModel'].value;
    if (!model) return;
    this.tests[kind]?.unsubscribe();
    const state = this.testState(kind);
    state.set({ state: 'running' });
    this.tests[kind] = this.llm.testModel(kind, model, this.baseUrl()).subscribe({
      next: (result) =>
        state.set(
          result.ok
            ? { state: 'ok', seconds: (result.elapsedMs / 1000).toFixed(1) }
            : { state: 'error', error: result.error ?? 'The model test failed.' },
        ),
      error: () => state.set({ state: 'error', error: 'The model test request failed.' }),
    });
  }

  cancelTest(kind: ModelKind): void {
    this.tests[kind]?.unsubscribe();
    delete this.tests[kind];
    this.testState(kind).set(IDLE);
  }

  save(): void {
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { chatModel, embeddingModel } = this.form.getRawValue();
    this.saving.set(true);
    // The API treats a missing value as "unchanged" and an empty name as "clear", so None is ''.
    this.setup
      .saveSettings({ chatModel: chatModel ?? '', embeddingModel: embeddingModel ?? '' })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.apply(settings);
          this.snackBar.open('Models saved', undefined, { duration: 3000 });
          this.setupState.refresh().subscribe();
          this.saved.emit(settings);
        },
        error: () => this.saving.set(false),
      });
  }

  private apply(settings: AppSettings): void {
    this.settings.set(settings);
    this.form.reset({
      chatModel: settings.chatModel ?? null,
      embeddingModel: settings.embeddingModel ?? null,
    });
  }
}

/** The server's models, plus the saved one when the server no longer lists it. */
function options(models: OllamaModel[], saved: string | null | undefined): ModelOption[] {
  const list = models.map((m) => ({ value: m.name, label: modelLabel(m) }));
  if (saved && !models.some((m) => m.name === saved)) {
    list.unshift({ value: saved, label: `${saved} (not found on the server)` });
  }
  return list;
}
