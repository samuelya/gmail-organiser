import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { GoogleAuthStatus, SetupService } from '../setup.service';

/** The result the OAuth callback hands back in `/setup?gmail=…&reason=…`. */
export type ConnectResult = { kind: 'connected' } | { kind: 'error'; reason: string };

const ERROR_MESSAGES: Record<string, string> = {
  missing_scopes:
    'Allow all requested permissions. Google did not grant every permission the organiser needs; try again and leave every box ticked.',
  access_denied: 'Access was denied on the Google consent screen. Try again and choose Allow.',
  state_mismatch:
    'The sign-in expired or was started in another tab. Try again from this page.',
  exchange_failed:
    'Google did not accept the sign-in. Check the client ID, secret and redirect URI, then try again.',
};

/** Readable message for a callback `reason`; unknown reasons get a generic message. */
export function connectErrorMessage(reason: string): string {
  return ERROR_MESSAGES[reason] ?? 'Connecting Gmail failed. Try again.';
}

/** Parses the callback query params; `null` when the page wasn't opened by the OAuth callback. */
export function parseConnectResult(gmail: string | null, reason: string | null): ConnectResult | null {
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
            <button mat-flat-button type="button" (click)="connect()" data-testid="retry">
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
            <button mat-stroked-button type="button" (click)="connect()">Reconnect</button>
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
            <p class="m-0">
              Opens Google's consent screen. Save the Google OAuth client in step 1 first.
            </p>
          }
          @if (!error()) {
            <div>
              <button mat-flat-button type="button" (click)="connect()" data-testid="connect">
                Connect Gmail
              </button>
            </div>
          }
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConnectGmailStep {
  private readonly setup = inject(SetupService);
  private readonly destroyRef = inject(DestroyRef);

  /** Set when the OAuth callback redirected back to the page. */
  readonly result = input<ConnectResult | null>(null);
  /** Emits the connection status after every load. */
  readonly statusChange = output<GoogleAuthStatus>();

  readonly status = signal<GoogleAuthStatus | null>(null);
  readonly busy = signal(false);
  private readonly dismissedError = signal(false);

  readonly error = computed(() => {
    const result = this.result();
    return result?.kind === 'error' && !this.dismissedError()
      ? connectErrorMessage(result.reason)
      : null;
  });

  constructor() {
    this.load();
  }

  connect(): void {
    this.setup.connectGoogle();
  }

  disconnect(): void {
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
