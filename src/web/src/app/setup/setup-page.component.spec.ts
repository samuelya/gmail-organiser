import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import {
  SetupPage,
  STEP_CONNECT_GMAIL,
  STEP_GOOGLE_CLIENT,
  STEP_MODELS,
  STEP_OLLAMA,
  STEP_SUMMARY,
} from './setup-page.component';
import { LlmService } from '../core/llm.service';
import {
  AppSettings,
  GoogleAuthStatus,
  SetupService,
  UpdateSettingsRequest,
} from './setup.service';
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
  let wizardSeen: boolean;
  let connectGoogle: ReturnType<typeof vi.fn<() => void>>;
  let saveSettings: ReturnType<typeof vi.fn<SetupService['saveSettings']>>;

  const settings = (): AppSettings => ({
    ollamaBaseUrl: 'http://ollama.example.com:11434',
    chatModel: null,
    embeddingModel: null,
    actionLabelName: 'Example-Action',
    deleteLabelName: 'Example-Delete',
    setupWizardSeen: wizardSeen,
    googleClient: { clientId: null, secretSet: false, lockedByEnv: false },
  });

  beforeEach(() => {
    connected = false;
    wizardSeen = false;
    connectGoogle = vi.fn<() => void>();
    saveSettings = vi.fn<SetupService['saveSettings']>((request: UpdateSettingsRequest) => {
      if (request.setupWizardSeen) wizardSeen = true;
      return of(settings());
    });
    const setup: Partial<SetupService> = {
      getSettings: () => of(settings()),
      saveSettings,
      getSetupStatus: () =>
        of({
          googleClientConfigured: true,
          gmailConnected: connected,
          gmailReauthRequired: false,
          ollamaReachable: false,
          chatModelSelected: false,
          embeddingModelSelected: false,
          wizardSeen,
          complete: false,
        }),
      getGoogleStatus: () => of(status(connected)),
      disconnectGoogle: () => {
        connected = false;
        return of(undefined);
      },
      connectGoogle,
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'setup', component: SetupPage },
          { path: 'dashboard', children: [] },
        ]),
        { provide: SetupService, useValue: setup },
        {
          provide: LlmService,
          useValue: {
            getModels: () =>
              of({
                reachable: false,
                version: null,
                chatModels: [],
                embeddingModels: [],
                error: 'Connection refused',
              }),
            testModel: vi.fn(),
          },
        },
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

  it('a saved Ollama URL completes step 3 and moves to the models', async () => {
    const { page, el, harness } = await open('/setup');
    page.completed[STEP_GOOGLE_CLIENT].set(true);
    page.completed[STEP_CONNECT_GMAIL].set(true);
    await harness.fixture.whenStable();
    page.stepper().selectedIndex = STEP_OLLAMA;
    await harness.fixture.whenStable();
    expect(selectedLabel(el)).toContain('Ollama URL');
    expect(page.ollamaUrl()).toBe('http://ollama.example.com:11434');
    q(el, 'save-url')!.click();
    await harness.fixture.whenStable();
    expect(saveSettings).toHaveBeenCalledWith({ ollamaBaseUrl: 'http://ollama.example.com:11434' });
    expect(page.completed[STEP_OLLAMA]()).toBe(true);
    expect(selectedLabel(el)).toContain('Models');
  });

  it('an already-saved Ollama URL completes step 3 without saving again', async () => {
    const { page, el, harness } = await open('/setup');
    page.completed[STEP_GOOGLE_CLIENT].set(true);
    page.completed[STEP_CONNECT_GMAIL].set(true);
    await harness.fixture.whenStable();
    page.stepper().selectedIndex = STEP_OLLAMA;
    await harness.fixture.whenStable();
    expect(page.completed[STEP_OLLAMA]()).toBe(true);
    expect(saveSettings).not.toHaveBeenCalled();
    // One Next button per step 1-4, in order: the third belongs to the Ollama URL step.
    const next = el.querySelectorAll<HTMLButtonElement>('button[matStepperNext]')[STEP_OLLAMA];
    expect(next.disabled).toBe(false);
  });

  it('the summary links back to a step', async () => {
    const { page, el, harness } = await open('/setup');
    [STEP_GOOGLE_CLIENT, STEP_CONNECT_GMAIL, STEP_OLLAMA, STEP_MODELS].forEach((i) =>
      page.completed[i].set(true),
    );
    await harness.fixture.whenStable();
    page.stepper().selectedIndex = STEP_SUMMARY;
    await harness.fixture.whenStable();
    expect(q(el, 'summary')).not.toBeNull();
    page.goTo(STEP_MODELS);
    await harness.fixture.whenStable();
    expect(selectedLabel(el)).toContain('Models');
  });

  it('Finish marks the wizard as seen and opens the dashboard', async () => {
    const { page, harness } = await open('/setup');
    page.finish();
    await harness.fixture.whenStable();
    expect(saveSettings).toHaveBeenCalledWith({ setupWizardSeen: true });
    expect(TestBed.inject(Router).url).toBe('/dashboard');
  });

  it('starts at step 1 without callback params', async () => {
    const { page } = await open('/setup');
    expect(page.connectResult).toBeNull();
    expect(page.initialIndex).toBe(STEP_GOOGLE_CLIENT);
  });

  it('?gmail=connected stays on step 2 with a confirmation and an enabled Next', async () => {
    connected = true;
    const { page, el } = await open('/setup?gmail=connected');
    expect(page.initialIndex).toBe(STEP_CONNECT_GMAIL);
    expect(selectedLabel(el)).toContain('Connect Gmail');
    expect(page.completed[STEP_GOOGLE_CLIENT]()).toBe(true);
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(true);
    expect(q(el, 'connect-success')!.textContent).toContain(`Gmail connected as ${EMAIL}.`);
    const next = el.querySelectorAll<HTMLButtonElement>('button[matStepperNext]')[
      STEP_CONNECT_GMAIL
    ];
    expect(next.disabled).toBe(false);
    expect(q(el, 'step-connected-as')!.textContent).toContain(`Connected as ${EMAIL}`);
    expect(TestBed.inject(Router).url).toBe('/setup');
  });

  it('?gmail=connected but the status says not connected: step 2 stays open and is selected', async () => {
    const { page, el } = await open('/setup?gmail=connected');
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(false);
    expect(selectedLabel(el)).toContain('Connect Gmail');
    expect(q(el, 'connect-success')).toBeNull();
    expect(q(el, 'connect')).not.toBeNull();
  });

  it('disconnect clears step 2 completion and the account', async () => {
    connected = true;
    const { page, el, harness } = await open('/setup?gmail=connected');
    page.onStatusChange(status(true));
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(true);
    page.onStatusChange(status(false));
    await harness.fixture.whenStable();
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(false);
    expect(q(el, 'step-connected-as')).toBeNull();
  });

  it('a skipped step 2 stays completed when not connected', async () => {
    const { page, harness } = await open('/setup');
    page.skip(STEP_GOOGLE_CLIENT);
    await harness.fixture.whenStable();
    page.skip(STEP_CONNECT_GMAIL);
    page.onStatusChange(status(false));
    expect(page.completed[STEP_CONNECT_GMAIL]()).toBe(true);
  });

  it('step 1 reports the saved client: completes it and passes it to step 2', async () => {
    const { page } = await open('/setup');
    page.onClientChange({ clientId: 'id.example.com', secretSet: true, lockedByEnv: false });
    expect(page.clientSaved()).toBe(true);
    expect(page.completed[STEP_GOOGLE_CLIENT]()).toBe(true);
    page.onClientChange({ clientId: null, secretSet: false, lockedByEnv: false });
    expect(page.clientSaved()).toBe(false);
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
