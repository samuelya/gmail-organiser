import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { GoogleAuthStatus, SetupService } from '../setup.service';
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

describe('ConnectGmailStep', () => {
  let current: GoogleAuthStatus;
  let setup: {
    getGoogleStatus: ReturnType<typeof vi.fn>;
    disconnectGoogle: ReturnType<typeof vi.fn>;
    connectGoogle: ReturnType<typeof vi.fn>;
  };

  async function render(initial: GoogleAuthStatus) {
    current = initial;
    setup = {
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
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('not connected: "Connect Gmail" navigates to the start endpoint', async () => {
    const { el } = await render(status({}));
    q(el, 'connect')!.click();
    expect(setup.connectGoogle).toHaveBeenCalled();
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

  it('reauth required: asks to connect again', async () => {
    const { el } = await render(status({ reason: 'reauth_required', accountEmail: EMAIL }));
    expect(q(el, 'reauth')!.textContent).toContain(EMAIL);
  });
});
