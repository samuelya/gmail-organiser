import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import {
  SetupPage,
  STEP_CONNECT_GMAIL,
  STEP_GOOGLE_CLIENT,
  STEP_OLLAMA,
} from './setup-page.component';
import { GoogleAuthStatus, SetupService } from './setup.service';
import { connectErrorMessage, parseConnectResult } from './steps/connect-gmail-step.component';

const EMAIL = 'user@example.com';

function status(connected: boolean): GoogleAuthStatus {
  return {
    connected,
    reason: connected ? null : 'not_connected',
    accountEmail: connected ? EMAIL : null,
    scopes: [],
    missingScopes: [],
    redirectUri: 'http://localhost:4200/api/auth/google/callback',
  };
}

describe('parseConnectResult / connectErrorMessage', () => {
  it('reads connected and error results', () => {
    expect(parseConnectResult('connected', null)).toEqual({ kind: 'connected' });
    expect(parseConnectResult('error', 'access_denied')).toEqual({
      kind: 'error',
      reason: 'access_denied',
    });
    expect(parseConnectResult(null, null)).toBeNull();
    expect(parseConnectResult('other', null)).toBeNull();
  });

  it('has a readable message per reason and a fallback', () => {
    expect(connectErrorMessage('missing_scopes')).toContain('Allow all requested permissions');
    const reasons = ['access_denied', 'state_mismatch', 'exchange_failed'];
    const messages = reasons.map(connectErrorMessage);
    expect(new Set(messages).size).toBe(reasons.length);
    expect(connectErrorMessage('something_new')).toContain('Connecting Gmail failed');
  });
});

describe('SetupPage', () => {
  let connected: boolean;
  let connectGoogle: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    connected = false;
    connectGoogle = vi.fn<() => void>();
    const setup: Partial<SetupService> = {
      getSettings: () =>
        of({
          ollamaBaseUrl: 'http://localhost:11434',
          chatModel: null,
          embeddingModel: null,
          actionLabelName: 'Example-Action',
          deleteLabelName: 'Example-Delete',
          setupWizardSeen: false,
          googleClient: { clientId: null, secretSet: false, lockedByEnv: false },
        }),
      getGoogleStatus: () => of(status(connected)),
      connectGoogle,
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'setup', component: SetupPage }]),
        { provide: SetupService, useValue: setup },
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
        },
      ],
    });
  });

  async function open(url: string) {
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl(url, SetupPage);
    await harness.fixture.whenStable();
    return { harness, page, el: harness.routeNativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  const selectedLabel = (el: HTMLElement) =>
    el.querySelector('.mat-step-header[aria-selected="true"]')?.textContent ?? '';

  it('skip completes the step and moves to the next one', async () => {
    const { page, el, harness } = await open('/setup');
    expect(selectedLabel(el)).toContain('Google OAuth client');
    page.skip(STEP_GOOGLE_CLIENT);
    await harness.fixture.whenStable();
    expect(selectedLabel(el)).toContain('Connect Gmail');
  });

  it('starts at step 1 without callback params', async () => {
    const { page } = await open('/setup');
    expect(page.connectResult).toBeNull();
    expect(page.initialIndex).toBe(STEP_GOOGLE_CLIENT);
  });

  it('?gmail=connected shows the account and moves on to step 3', async () => {
    connected = true;
    const { page, el } = await open('/setup?gmail=connected');
    expect(page.initialIndex).toBe(STEP_OLLAMA);
    expect(selectedLabel(el)).toContain('Ollama URL');
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(true);
    expect(q(el, 'step-connected-as')!.textContent).toContain(`Connected as ${EMAIL}`);
    expect(TestBed.inject(Router).url).toBe('/setup');
  });

  for (const reason of ['missing_scopes', 'access_denied', 'state_mismatch', 'exchange_failed']) {
    it(`?gmail=error&reason=${reason} shows its message and a Retry button on step 2`, async () => {
      const { page, el, harness } = await open(`/setup?gmail=error&reason=${reason}`);
      expect(page.initialIndex).toBe(STEP_CONNECT_GMAIL);
      expect(selectedLabel(el)).toContain('Connect Gmail');
      expect(q(el, 'connect-error')!.textContent).toContain(connectErrorMessage(reason));
      expect(TestBed.inject(Router).url).toBe('/setup');

      q(el, 'retry')!.click();
      await harness.fixture.whenStable();
      expect(connectGoogle).toHaveBeenCalled();
    });
  }
});
