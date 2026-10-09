import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { HttpErrorResponse } from '@angular/common/http';
import {
  catchError,
  concatMap,
  EMPTY,
  filter,
  finalize,
  map,
  Observable,
  of,
  Subscription,
  switchMap,
} from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { ClaudeApiModels, LlmService } from '../core/llm.service';
import { ModelTest, testSeconds } from '../setup/steps/models-step.component';
import { LlmProvider, LlmProviderUpdate } from './llm-provider.models';
import { SettingsService } from './settings.service';
import { SettingsDto } from './settings.models';
import { problemErrors } from './triage-settings.models';

/** Shown on a failed save that has no field message. */
const NOT_SAVED = 'Not saved. Try again.';
const NO_KEY = 'Save an API key first.';

const IDLE: ModelTest = { state: 'idle' };

/**
 * The Settings page's analysis model provider (#490): Ollama on this machine or the Claude API, and
 * the write-only Claude API key and the Claude model (#491), picked from the account's list. The key
 * lives only in its form control and is never read back; the page passes the loaded settings in and
 * takes each saved `SettingsDto` back.
 */
@Component({
  selector: 'app-llm-provider-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatRadioModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  templateUrl: './llm-provider-settings.component.html',
  styles: `
    fieldset {
      border: 0;
      margin: 0;
      padding: 0;
    }
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .warn {
      color: var(--mat-sys-error);
    }
    .banner {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      border-radius: var(--mat-sys-corner-medium);
    }
    a {
      color: inherit;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmProviderSettingsSection {
  private readonly settingsApi = inject(SettingsService);
  private readonly llm = inject(LlmService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** The page's loaded settings; `null` until loaded keeps the section disabled. */
  readonly settings = input<SettingsDto | null>(null);
  readonly saved = output<SettingsDto>();
  /** Asks the page to scroll to another card, by its heading id. */
  readonly jump = output<string>();

  readonly providers: readonly { value: LlmProvider; label: string }[] = [
    { value: 'ollama', label: 'Ollama (local)' },
    { value: 'claude_api', label: 'Claude API (cloud)' },
  ];

  readonly provider = new FormControl<LlmProvider>('ollama', { nonNullable: true });
  /** The key is kept only here and cleared after every save and on destroy. */
  readonly apiKey = new FormControl('', { nonNullable: true, validators: Validators.required });
  /** The Claude model; never auto-selected, the owner picks. */
  readonly model = new FormControl<string | null>(null);
  readonly providerForm = new FormGroup({ provider: this.provider });
  readonly keyForm = new FormGroup({ apiKey: this.apiKey });

  /** The provider picked on screen, saved or not: the notices follow it. */
  readonly selected = toSignal(this.provider.valueChanges, {
    initialValue: this.provider.value,
  });
  readonly savedProvider = computed(() => this.settings()?.llmProvider ?? null);
  readonly keySet = computed(() => this.settings()?.claudeApiKeySet ?? false);
  readonly keyHint = computed(() => this.settings()?.claudeApiKeyHint ?? null);
  readonly savedModel = computed(() => this.settings()?.claudeApiModel ?? null);
  readonly modelValue = toSignal(this.model.valueChanges, { initialValue: this.model.value });
  /** Analysis on the Claude API needs both a key and a model. */
  readonly incomplete = computed(() => {
    const s = this.settings();
    return s?.llmProvider === 'claude_api' && (!s.claudeApiKeySet || !s.claudeApiModel);
  });

  readonly providerSaving = signal(false);
  readonly providerError = signal<string | null>(null);
  readonly keySaving = signal(false);
  /** Set when a key change was saved but the settings could not be read again. */
  readonly keyReloadError = signal<string | null>(null);
  /** Shows the key input over a saved key. */
  readonly replacing = signal(false);
  readonly showKeyInput = computed(() => !this.keySet() || this.replacing());

  /**
   * The last model list, kept while a reload runs and across provider switches; `null` until one is
   * loaded and again once the key is removed.
   */
  readonly models = signal<ClaudeApiModels | null>(null);
  readonly modelsLoading = signal(false);
  /** Bumped to load the list again (Refresh, a key save or replace). */
  private readonly modelsReload = signal(0);
  /** The reload count of the reachable list in `models`, so a provider switch doesn't load again. */
  private modelsLoadedAt: number | null = null;
  /**
   * The list as options. The saved model and the current (unsaved) pick stay even when the list
   * doesn't have them, so the select never blanks; one the account no longer lists is marked.
   */
  readonly modelOptions = computed(() => {
    const list = this.models();
    const options = (list?.models ?? []).map((m) => ({
      value: m.id,
      label: `${m.displayName} (${m.id})`,
    }));
    for (const extra of [this.savedModel(), this.modelValue()]) {
      if (extra && !options.some((o) => o.value === extra)) {
        options.push({ value: extra, label: list?.reachable ? `${extra} (not available)` : extra });
      }
    }
    return options;
  });
  readonly modelSelectable = computed(
    () => this.keySet() && !this.modelsLoading() && this.models()?.reachable === true,
  );
  readonly modelTest = signal<ModelTest>(IDLE);
  private testRun: Subscription | null = null;
  /** Save sends what differs from the saved settings: the provider, and the model on Claude API. */
  readonly changes = computed<LlmProviderUpdate>(() => {
    const changes: LlmProviderUpdate = {};
    const provider = this.selected();
    if (provider !== this.savedProvider()) changes.llmProvider = provider;
    const model = this.modelValue();
    if (provider === 'claude_api' && model && model !== this.savedModel()) {
      changes.claudeApiModel = model;
    }
    return changes;
  });
  readonly dirty = computed(() => Object.keys(this.changes()).length > 0);

  constructor() {
    this.provider.disable();
    // Only a change of the saved provider resets the radio, so a key save keeps an unsaved pick.
    effect(() => {
      const saved = this.savedProvider();
      if (saved === null) return;
      this.provider.setValue(saved);
      this.provider.enable();
    });
    // Likewise only a change of the saved model resets the select.
    effect(() => this.model.setValue(this.savedModel()));
    // No events: a load finishing must not look like a new pick and cancel a running test.
    effect(() => {
      if (this.modelSelectable()) this.model.enable({ emitEvent: false });
      else this.model.disable({ emitEvent: false });
    });
    // A load per Refresh or key change; a switch back to Claude API reuses a reachable list, and
    // switchMap drops a list no longer wanted (Ollama picked, key removed).
    toObservable(
      computed(() => ({
        reload: this.modelsReload(),
        load: this.selected() === 'claude_api' && this.keySet(),
      })),
    )
      .pipe(
        switchMap(({ reload, load }) => {
          this.modelsLoading.set(false);
          if (!this.keySet()) {
            this.models.set(null);
            this.modelsLoadedAt = null;
          }
          if (!load || this.modelsLoadedAt === reload) return EMPTY;
          this.modelsLoading.set(true);
          return this.llm.getClaudeApiModels().pipe(
            // The error interceptor shows why the request itself failed.
            catchError(() =>
              of<ClaudeApiModels>({
                keySet: true,
                reachable: false,
                error: 'Could not load the Claude models.',
                models: [],
              }),
            ),
            finalize(() => this.modelsLoading.set(false)),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((list) => {
        this.models.set(list);
        this.modelsLoadedAt = list.reachable ? this.modelsReload() : null;
      });
    this.model.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.cancelTest());
    // A new pick drops the last save error; leaving Claude API drops a typed key and Replace mode.
    this.provider.valueChanges.pipe(takeUntilDestroyed()).subscribe((value) => {
      this.providerError.set(null);
      if (value !== 'claude_api') {
        this.cancelReplace();
        this.cancelTest();
      }
    });
    this.destroyRef.onDestroy(() => {
      this.apiKey.reset();
      this.testRun?.unsubscribe();
    });
  }

  refreshModels(): void {
    this.modelsReload.update((n) => n + 1);
  }

  testModel(): void {
    const model = this.model.value;
    if (!model || this.modelTest().state === 'running') return;
    this.modelTest.set({ state: 'running' });
    this.testRun = this.llm
      .testClaudeApiModel(model)
      .pipe(
        map((result): ModelTest =>
          result.ok
            ? { state: 'ok', seconds: testSeconds(result.elapsedMs) }
            : { state: 'error', error: result.error ?? 'The model test failed.' },
        ),
        // 409 and 400 are quiet and shown here; the error interceptor reports any other failure.
        catchError((error: unknown) => of(testFailure(error))),
      )
      .subscribe((outcome) => this.modelTest.set(outcome));
  }

  saveProvider(): void {
    const changes = this.changes();
    if (this.providerSaving() || !this.dirty()) return;
    this.providerSaving.set(true);
    this.providerError.set(null);
    this.settingsApi
      .saveLlmProvider(changes)
      .pipe(
        finalize(() => this.providerSaving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (settings) => {
          this.saved.emit(settings);
          this.snackBar.open('Analysis model provider saved', undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          const errors = problemErrors(error);
          this.providerError.set(
            errors['llmProvider']?.[0] ??
              errors['claudeApiModel']?.[0] ??
              errors['']?.[0] ??
              NOT_SAVED,
          );
        },
      });
  }

  saveKey(): void {
    if (this.keySaving() || this.apiKey.invalid) {
      this.apiKey.markAsTouched();
      return;
    }
    this.keySaving.set(true);
    this.updateKey(this.llm.setClaudeApiKey(this.apiKey.value), 'Claude API key saved', (error) => {
      const errors = problemErrors(error);
      this.apiKey.setErrors({ server: errors['apiKey']?.[0] ?? errors['']?.[0] ?? NOT_SAVED });
      this.apiKey.markAsTouched();
    });
  }

  replaceKey(): void {
    this.apiKey.reset();
    this.replacing.set(true);
  }

  cancelReplace(): void {
    this.apiKey.reset();
    this.replacing.set(false);
  }

  removeKey(): void {
    openConfirm(this.dialog, {
      title: 'Remove the Claude API key?',
      message: 'Analysis on the Claude API stops working until a new key is saved.',
      confirm: 'Remove',
    })
      .pipe(filter(Boolean), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.keySaving.set(true);
        // The error interceptor shows why a remove failed.
        this.updateKey(this.llm.clearClaudeApiKey(), 'Claude API key removed', () => undefined);
      });
  }

  private cancelTest(): void {
    this.testRun?.unsubscribe();
    this.testRun = null;
    this.modelTest.set(IDLE);
  }

  /** Runs a key change, then reads the settings again for the new key state and hint. */
  private updateKey(
    request: Observable<void>,
    message: string,
    onError: (error: unknown) => void,
  ): void {
    this.keyReloadError.set(null);
    request
      .pipe(
        concatMap(() => {
          this.apiKey.reset();
          this.replacing.set(false);
          this.snackBar.open(message, undefined, { duration: 3000 });
          return this.settingsApi.reloadSettings().pipe(
            catchError(() => {
              // The change itself was saved; the key state shown here is now out of date.
              this.keyReloadError.set(
                'Saved, but the key state could not be loaded. Reload the page to see it.',
              );
              return EMPTY;
            }),
          );
        }),
        finalize(() => this.keySaving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (settings) => {
          this.saved.emit(settings);
          this.refreshModels();
        },
        error: onError,
      });
  }
}

/** A failed model test request as shown next to Test; idle when the error interceptor reported it. */
function testFailure(error: unknown): ModelTest {
  if (!(error instanceof HttpErrorResponse)) return IDLE;
  if (error.status === 409) return { state: 'error', error: NO_KEY };
  if (error.status === 400) {
    return {
      state: 'error',
      error: problemErrors(error)['model']?.[0] ?? 'The model test failed.',
    };
  }
  return IDLE;
}
