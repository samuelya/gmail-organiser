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
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, EMPTY, filter, finalize, Observable } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { LlmService } from '../core/llm.service';
import { LlmProvider } from './llm-provider.models';
import { SettingsService } from './settings.service';
import { SettingsDto } from './settings.models';
import { problemErrors } from './triage-settings.models';

/** Shown on a failed save that has no field message. */
const NOT_SAVED = 'Not saved. Try again.';

/**
 * The Settings page's analysis model provider (#490): Ollama on this machine or the Claude API, and
 * the write-only Claude API key. The key lives only in its form control and is never read back; the
 * page passes the loaded settings in and takes each saved `SettingsDto` back.
 */
@Component({
  selector: 'app-llm-provider-settings',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatRadioModule,
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
  readonly providerForm = new FormGroup({ provider: this.provider });
  readonly keyForm = new FormGroup({ apiKey: this.apiKey });

  /** The provider picked on screen, saved or not: the notices follow it. */
  readonly selected = toSignal(this.provider.valueChanges, {
    initialValue: this.provider.value,
  });
  readonly savedProvider = computed(() => this.settings()?.llmProvider ?? null);
  readonly keySet = computed(() => this.settings()?.claudeApiKeySet ?? false);
  readonly keyHint = computed(() => this.settings()?.claudeApiKeyHint ?? null);
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

  constructor() {
    this.provider.disable();
    // Only a change of the saved provider resets the radio, so a key save keeps an unsaved pick.
    effect(() => {
      const saved = this.savedProvider();
      if (saved === null) return;
      this.provider.setValue(saved);
      this.provider.enable();
    });
    // A new pick drops the last save error; leaving Claude API drops a typed key and Replace mode.
    this.provider.valueChanges.pipe(takeUntilDestroyed()).subscribe((value) => {
      this.providerError.set(null);
      if (value !== 'claude_api') this.cancelReplace();
    });
    this.destroyRef.onDestroy(() => this.apiKey.reset());
  }

  saveProvider(): void {
    const llmProvider = this.provider.value;
    if (this.providerSaving() || llmProvider === this.savedProvider()) return;
    this.providerSaving.set(true);
    this.providerError.set(null);
    this.settingsApi
      .saveLlmProvider({ llmProvider })
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
          this.providerError.set(errors['llmProvider']?.[0] ?? errors['']?.[0] ?? NOT_SAVED);
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
      .subscribe({ next: (settings) => this.saved.emit(settings), error: onError });
  }
}
