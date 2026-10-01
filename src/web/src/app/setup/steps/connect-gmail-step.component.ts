import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  output,
  OnInit,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { filter } from 'rxjs';
import { openConfirm } from '../../core/confirm-dialog';
import { GoogleAuthStatus, SetupService } from '../setup.service';

/** The result the OAuth callback hands back in `/setup?gmail=…&reason=…`. */
export type ConnectResult = { kind: 'connected' } | { kind: 'error'; reason: string };

const ERROR_MESSAGES: Record<string, string> = {
  missing_scopes:
    'Allow all requested permissions. Google did not grant every permission the organiser needs; try again and leave every box ticked.',
  access_denied: 'Access was denied on the Google consent screen. Try again and choose Allow.',
  state_mismatch: 'The sign-in expired or was started in another tab. Try again from this page.',
  exchange_failed:
    'Google did not accept the sign-in. Check the client ID, secret and redirect URI, then try again.',
};

/** Readable message for a callback `reason`; unknown reasons get a generic message. */
export function connectErrorMessage(reason: string): string {
  return ERROR_MESSAGES[reason] ?? 'Connecting Gmail failed. Try again.';
}

/** Parses the callback query params; `null` when the page wasn't opened by the OAuth callback. */
export function parseConnectResult(
  gmail: string | null,
  reason: string | null,
): ConnectResult | null {
  if (gmail === 'connected') return { kind: 'connected' };
  if (gmail === 'error') return { kind: 'error', reason: reason ?? '' };
  return null;
}

/** Step 2: connect, reconnect or disconnect Gmail. Standalone so Settings can reuse it. */
@Component({
  selector: 'app-connect-gmail-step',
  imports: [MatButtonModule, MatIconModule],
  template: `
    <div class="flex max-w-2xl flex-col gap-3">
      @if (error(); as message) {
        <div role="alert" class="flex items-start gap-2" data-testid="connect-error">
          <mat-icon aria-hidden="true" class="shrink-0">error</mat-icon>
          <div class="flex flex-col items-start gap-2">
            <p class="m-0">{{ message }}</p>
            <button
              mat-flat-button
              type="button"
              (click)="connect()"
              [disabled]="!clientConfigured()"
              [disabledInteractive]="true"
              [attr.aria-describedby]="clientConfigured() ? null : noClientId"
              data-testid="retry"
            >
              Retry
            </button>
          </div>
        </div>
      }

      @if (status(); as s) {
        @if (s.connected) {
          <p class="m-0 flex items-center gap-2" role="status" data-testid="connected-as">
            <mat-icon aria-hidden="true">check_circle</mat-icon>
            Connected as {{ s.accountEmail }}
          </p>
          <div class="flex flex-wrap gap-2">
            <button
              mat-stroked-button
              type="button"
              (click)="connect()"
              [disabled]="!clientConfigured()"
              [disabledInteractive]="true"
              [attr.aria-describedby]="clientConfigured() ? null : noClientId"
              data-testid="reconnect"
            >
              Reconnect
            </button>
            <button
              mat-button
              type="button"
              (click)="disconnect()"
              [disabled]="busy()"
              data-testid="disconnect"
            >
              Disconnect
            </button>
          </div>
        } @else {
          @if (s.reason === 'reauth_required') {
            <p class="m-0" data-testid="reauth">
              Gmail access for {{ s.accountEmail }} has expired or was revoked. Connect again.
            </p>
          } @else {
            <p class="m-0">Opens Google's consent screen.</p>
          }
          @if (!error()) {
            <div>
              <button
                mat-flat-button
                type="button"
                (click)="connect()"
                [disabled]="!clientConfigured()"
                [disabledInteractive]="true"
                [attr.aria-describedby]="clientConfigured() ? null : noClientId"
                data-testid="connect"
              >
                Connect Gmail
              </button>
            </div>
          }
        }
      }

      @if (!clientConfigured()) {
        <p class="m-0 flex items-center gap-2" [id]="noClientId" data-testid="no-client">
          <mat-icon aria-hidden="true">info</mat-icon>
          @if (clientChecked()) {
            Save the Google OAuth client {{ clientLocation() }} before connecting.
          } @else {
            Checking the Google OAuth client…
          }
        </p>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConnectGmailStep implements OnInit {
  private readonly setup = inject(SetupService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);

  /** Set when the OAuth callback redirected back to the page. */
  readonly result = input<ConnectResult | null>(null);
  /** Set by the host once the Google OAuth client is saved (the wizard's step 1). */
  readonly clientSaved = input(false);
  /** Where the host page lets the user save the Google OAuth client. */
  readonly clientLocation = input('in step 1');
  /** Ask before disconnecting (Settings); the wizard disconnects directly. */
  readonly confirmDisconnect = input(false);
  /** Emits the connection status after every load. */
  readonly statusChange = output<GoogleAuthStatus>();

  readonly status = signal<GoogleAuthStatus | null>(null);
  readonly busy = signal(false);
  private readonly dismissedError = signal(false);
  /** `googleClientConfigured` from the setup status: also true with the fake Gmail, which needs no client. */
  private readonly clientConfiguredByApi = signal<boolean | null>(null);

  readonly noClientId = 'connect-gmail-no-client';
  /** Without a client the start endpoint answers 409 JSON instead of redirecting, so Connect waits for one. */
  readonly clientConfigured = computed(
    () => this.clientSaved() || this.clientConfiguredByApi() === true,
  );
  readonly clientChecked = computed(() => this.clientConfiguredByApi() !== null);

  readonly error = computed(() => {
    const result = this.result();
    return result?.kind === 'error' && !this.dismissedError()
      ? connectErrorMessage(result.reason)
      : null;
  });

  // Loads in ngOnInit (not the constructor) so the outputs have listeners when the first value arrives.
  ngOnInit(): void {
    this.load();
    this.setup
      .getSetupStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (s) => this.clientConfiguredByApi.set(s.googleClientConfigured),
        error: () => this.clientConfiguredByApi.set(false),
      });
  }

  connect(): void {
    if (!this.clientConfigured()) return;
    this.setup.connectGoogle();
  }

  disconnect(): void {
    if (!this.confirmDisconnect()) {
      this.doDisconnect();
      return;
    }
    openConfirm(this.dialog, {
      title: 'Disconnect Gmail?',
      message:
        'The organiser’s access to Gmail is revoked until you connect again. Your mail is not changed.',
      confirm: 'Disconnect',
    })
      .pipe(
        filter((confirmed) => confirmed),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.doDisconnect());
  }

  private doDisconnect(): void {
    this.busy.set(true);
    this.setup
      .disconnectGoogle()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.dismissedError.set(true);
          this.load();
        },
        error: () => this.busy.set(false),
      });
  }

  private load(): void {
    this.setup
      .getGoogleStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((status) => {
        this.status.set(status);
        this.statusChange.emit(status);
      });
  }
}
