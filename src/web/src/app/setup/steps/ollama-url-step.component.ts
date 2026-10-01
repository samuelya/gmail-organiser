import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import { LlmModels, LlmService } from '../../core/llm.service';
import { SetupService } from '../setup.service';

/** Same rule as the API: an absolute http or https URL. */
export function httpUrlValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = (control.value ?? '').trim();
  if (!value) return null; // `required` reports the empty case
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? null : { httpUrl: true };
  } catch {
    return { httpUrl: true };
  }
}

/**
 * The Ollama server URL: "Test URL" checks an unsaved value, "Save" stores it.
 * Has no stepper code, so the Settings page embeds it as is.
 */
@Component({
  selector: 'app-ollama-url-step',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './ollama-url-step.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OllamaUrlStep implements OnInit {
  private readonly setup = inject(SetupService);
  private readonly llm = inject(LlmService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private testSub: Subscription | null = null;

  /** The saved URL, after it loads and after every save. */
  readonly urlChange = output<string>();
  /** Emits after a successful save. */
  readonly saved = output<string>();

  readonly loaded = signal(false);
  readonly saving = signal(false);
  readonly testing = signal(false);
  readonly result = signal<LlmModels | null>(null);

  readonly url = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, httpUrlValidator],
  });

  constructor() {
    this.destroyRef.onDestroy(() => this.testSub?.unsubscribe());
    // A test result belongs to the URL it was run for.
    this.url.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.result.set(null));
  }

  ngOnInit(): void {
    this.setup
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => {
        this.url.reset(settings.ollamaBaseUrl);
        this.loaded.set(true);
        this.urlChange.emit(settings.ollamaBaseUrl);
      });
  }

  test(): void {
    if (this.url.invalid) {
      this.url.markAsTouched();
      return;
    }
    this.testSub?.unsubscribe();
    this.testing.set(true);
    this.testSub = this.llm.getModels(this.url.value.trim()).subscribe({
      next: (models) => {
        this.testing.set(false);
        this.result.set(models);
      },
      error: () => this.testing.set(false),
    });
  }

  save(): void {
    if (this.saving()) return;
    if (this.url.invalid) {
      this.url.markAsTouched();
      return;
    }
    const url = this.url.value.trim();
    this.saving.set(true);
    this.setup
      .saveSettings({ ollamaBaseUrl: url })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.url.reset(settings.ollamaBaseUrl, { emitEvent: false });
          this.snackBar.open('Ollama URL saved', undefined, { duration: 3000 });
          this.urlChange.emit(settings.ollamaBaseUrl);
          this.saved.emit(settings.ollamaBaseUrl);
        },
        error: () => this.saving.set(false),
      });
  }
}
