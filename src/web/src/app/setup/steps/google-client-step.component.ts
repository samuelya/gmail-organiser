import { Clipboard } from '@angular/cdk/clipboard';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  output,
  signal,
} from '@angular/core';
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
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { GoogleClientSettings, SetupService } from '../setup.service';

export const GOOGLE_CLIENT_ID_SUFFIX = '.apps.googleusercontent.com';
/** Repository-relative path of the guide for creating the Google OAuth client. */
export const GOOGLE_OAUTH_GUIDE = 'docs/setup/google-oauth.md';
export const SAVED_SECRET_MASK = '•••• saved';

export function googleClientIdValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = (control.value ?? '').trim();
  if (!value) return null; // `required` reports the empty case
  return value.endsWith(GOOGLE_CLIENT_ID_SUFFIX) && value.length > GOOGLE_CLIENT_ID_SUFFIX.length
    ? null
    : { googleClientId: true };
}

/**
 * Step 1: the Google OAuth client ID and secret, plus the redirect URI to register.
 * Standalone so Settings can reuse it. The secret is never shown back: once saved it reads "•••• saved".
 */
@Component({
  selector: 'app-google-client-step',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  templateUrl: './google-client-step.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GoogleClientStep {
  private readonly setup = inject(SetupService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly clipboard = inject(Clipboard);
  private readonly destroyRef = inject(DestroyRef);

  /** Emits the client state after it loads and after every save. */
  readonly clientChange = output<GoogleClientSettings>();

  readonly client = signal<GoogleClientSettings | null>(null);
  readonly redirectUri = signal<string | null>(null);
  readonly replacing = signal(false);
  readonly saving = signal(false);

  readonly suffix = GOOGLE_CLIENT_ID_SUFFIX;
  readonly guide = GOOGLE_OAUTH_GUIDE;
  readonly secretMask = SAVED_SECRET_MASK;

  readonly locked = computed(() => this.client()?.lockedByEnv ?? false);
  readonly secretSet = computed(() => this.client()?.secretSet ?? false);
  /** Editable unless `.env` locks the client, or a secret is saved and the user hasn't chosen "Replace". */
  readonly editing = computed(
    () => this.client() !== null && !this.locked() && (!this.secretSet() || this.replacing()),
  );

  readonly form = new FormGroup({
    clientId: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, googleClientIdValidator],
    }),
    // Required whenever the form is editable: the API stores the ID and secret together.
    clientSecret: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  constructor() {
    this.setup
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => this.apply(settings.googleClient));
    this.setup
      .getGoogleStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((status) => this.redirectUri.set(status.redirectUri));
  }

  replace(): void {
    this.replacing.set(true);
    this.form.reset({ clientId: this.client()?.clientId ?? '', clientSecret: '' });
  }

  cancelReplace(): void {
    this.replacing.set(false);
    this.form.reset({ clientId: this.client()?.clientId ?? '', clientSecret: '' });
  }

  save(): void {
    if (!this.editing() || this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { clientId, clientSecret } = this.form.getRawValue();
    this.saving.set(true);
    this.setup
      .saveGoogleClient({ clientId: clientId.trim(), clientSecret: clientSecret.trim() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.replacing.set(false);
          this.apply(settings.googleClient);
          this.snackBar.open('Google client saved', undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.showServerErrors(error);
        },
      });
  }

  copyRedirectUri(): void {
    const uri = this.redirectUri();
    if (uri && this.clipboard.copy(uri)) {
      this.snackBar.open('Redirect URI copied', undefined, { duration: 2000 });
    }
  }

  private apply(client: GoogleClientSettings): void {
    this.client.set(client);
    this.form.reset({ clientId: client.clientId ?? '', clientSecret: '' });
    this.clientChange.emit(client);
  }

  /** Maps ValidationProblem `errors` (`clientId`, `clientSecret`) onto the matching controls. */
  private showServerErrors(error: unknown): void {
    if (!(error instanceof HttpErrorResponse) || error.status !== 400) return;
    const errors = (error.error as { errors?: Record<string, string[]> } | null)?.errors ?? {};
    for (const [key, messages] of Object.entries(errors)) {
      const control = this.form.get(key);
      if (control && messages.length > 0) {
        control.setErrors({ server: messages[0] });
        control.markAsTouched();
      }
    }
  }
}
