import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
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

/** Same bounds and step as the API's `fetchChunkSize` validation. */
export const FETCH_CHUNK = { min: 100, max: 5000, step: 100 } as const;

/** A whole multiple of `step`. */
export function stepValidator(step: number) {
  return (control: { value: number | null }) =>
    control.value === null || control.value % step === 0 ? null : { step: true };
}

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
        stepValidator(FETCH_CHUNK.step),
      ],
    }),
  });
  readonly fetchChunkSize = this.fetchForm.controls.fetchChunkSize;

  /** The Google section reports a saved client ID and secret; the Gmail section enables Connect from it. */
  readonly clientSaved = signal(false);
  /** `null` until the Ollama URL is saved here: the models section lists against the saved URL. */
  readonly ollamaUrl = signal<string | null>(null);

  ngOnInit(): void {
    this.setup
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.fetchChunkSize.reset(settings.fetchChunkSize);
          this.fetchLoaded.set(true);
        },
        // The error interceptor shows why; the field stays disabled without a saved value.
        error: () => undefined,
      });
  }

  onClientChange(client: GoogleClientSettings): void {
    this.clientSaved.set(!!client.clientId && client.secretSet);
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
