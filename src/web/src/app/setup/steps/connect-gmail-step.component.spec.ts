import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { GoogleAuthStatus, SetupService, SetupStatus } from '../setup.service';
import { ConnectGmailStep } from './connect-gmail-step.component';

const EMAIL = 'user@example.com';

function status(over: Partial<GoogleAuthStatus>): GoogleAuthStatus {
  return {
    connected: false,
    reason: 'not_connected',
    accountEmail: null,
    scopes: [],
    missingScopes: [],
    redirectUri: 'http://localhost:4200/api/auth/google/callback',
    ...over,
  };
}

function setupStatus(googleClientConfigured: boolean): SetupStatus {
  return {
    googleClientConfigured,
    gmailConnected: false,
    gmailReauthRequired: false,
    ollamaReachable: false,
    chatModelSelected: false,
    embeddingModelSelected: false,
    wizardSeen: false,
    complete: false,
  };
}

describe('ConnectGmailStep', () => {
  let current: GoogleAuthStatus;
  let setup: {
    getSetupStatus: ReturnType<typeof vi.fn>;
    getGoogleStatus: ReturnType<typeof vi.fn>;
    disconnectGoogle: ReturnType<typeof vi.fn>;
    connectGoogle: ReturnType<typeof vi.fn>;
  };

  async function render(
    initial: GoogleAuthStatus,
    opts: { configured?: boolean; statusFails?: boolean; clientSaved?: boolean } = {},
  ) {
    current = initial;
    setup = {
      getSetupStatus: vi.fn(() =>
        opts.statusFails
          ? throwError(() => new Error('down'))
          : of(setupStatus(opts.configured ?? true)),
      ),
      getGoogleStatus: vi.fn(() => of(current)),
      disconnectGoogle: vi.fn(() => {
        current = status({});
        return of(undefined);
      }),
      connectGoogle: vi.fn(),
    };
    TestBed.configureTestingModule({
      imports: [ConnectGmailStep],
      providers: [{ provide: SetupService, useValue: setup }],
    });
    const fixture = TestBed.createComponent(ConnectGmailStep);
    if (opts.clientSaved) fixture.componentRef.setInput('clientSaved', true);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('not connected: "Connect Gmail" navigates to the start endpoint', async () => {
    const { el } = await render(status({}));
    q(el, 'connect')!.click();
    expect(setup.connectGoogle).toHaveBeenCalledWith('setup');
  });

  it('returnTo settings: Reconnect returns to Settings', async () => {
    const { fixture, el } = await render(
      status({ connected: true, reason: null, accountEmail: EMAIL }),
    );
    fixture.componentRef.setInput('returnTo', 'settings');
    await fixture.whenStable();
    q(el, 'reconnect')!.click();
    expect(setup.connectGoogle).toHaveBeenCalledWith('settings');
  });

  it('connected: shows the account; Disconnect posts and reloads the status', async () => {
    const { fixture, el } = await render(
      status({ connected: true, reason: null, accountEmail: EMAIL }),
    );
    expect(q(el, 'connected-as')!.textContent).toContain(`Connected as ${EMAIL}`);
    expect(el.textContent).toContain('Reconnect');

    q(el, 'disconnect')!.click();
    await fixture.whenStable();
    expect(setup.disconnectGoogle).toHaveBeenCalled();
    expect(setup.getGoogleStatus).toHaveBeenCalledTimes(2);
    expect(q(el, 'connected-as')).toBeNull();
    expect(q(el, 'connect')).not.toBeNull();
  });

  it('connected callback result: confirms the connection until a disconnect', async () => {
    const { fixture, el } = await render(
      status({ connected: true, reason: null, accountEmail: EMAIL }),
    );
    fixture.componentRef.setInput('result', { kind: 'connected' });
    await fixture.whenStable();
    const success = q(el, 'connect-success')!;
    expect(success.textContent).toContain(`Gmail connected as ${EMAIL}.`);
    expect(success.getAttribute('role')).toBe('status');
    expect(q(el, 'connect-error')).toBeNull();

    q(el, 'disconnect')!.click();
    await fixture.whenStable();
    expect(q(el, 'connect-success')).toBeNull();
    expect(q(el, 'connect')).not.toBeNull();
  });

  it('connected callback result but not connected: no confirmation, Connect shown', async () => {
    const { fixture, el } = await render(status({}));
    fixture.componentRef.setInput('result', { kind: 'connected' });
    await fixture.whenStable();
    expect(q(el, 'connect-success')).toBeNull();
    expect(q(el, 'connect')).not.toBeNull();
  });

  it('no client configured: Connect is disabled with an explanation and does not navigate', async () => {
    const { el } = await render(status({}), { configured: false });
    const button = q(el, 'connect')!;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(q(el, 'no-client')!.textContent).toContain('Save the Google OAuth client in step 1');
    expect(button.getAttribute('aria-describedby')).toBe(q(el, 'no-client')!.id);
    button.click();
    expect(setup.connectGoogle).not.toHaveBeenCalled();
  });

  it('no client: Retry after a callback error and Reconnect are disabled too', async () => {
    const { fixture, el } = await render(
      status({ connected: true, reason: null, accountEmail: EMAIL }),
      { configured: false },
    );
    q(el, 'reconnect')!.click();
    fixture.componentRef.setInput('result', { kind: 'error', reason: 'access_denied' });
    await fixture.whenStable();
    q(el, 'retry')!.click();
    expect(q(el, 'retry')!.getAttribute('aria-disabled')).toBe('true');
    expect(setup.connectGoogle).not.toHaveBeenCalled();
  });

  it('setup status fails: Connect stays disabled', async () => {
    const { el } = await render(status({}), { statusFails: true });
    q(el, 'connect')!.click();
    expect(setup.connectGoogle).not.toHaveBeenCalled();
    expect(q(el, 'no-client')).not.toBeNull();
  });

  it('client saved by the host enables Connect before the API reports it', async () => {
    const { el } = await render(status({}), { configured: false, clientSaved: true });
    expect(q(el, 'no-client')).toBeNull();
    q(el, 'connect')!.click();
    expect(setup.connectGoogle).toHaveBeenCalled();
  });

  it('reauth required: asks to connect again', async () => {
    const { el } = await render(status({ reason: 'reauth_required', accountEmail: EMAIL }));
    expect(q(el, 'reauth')!.textContent).toContain(EMAIL);
  });
});
