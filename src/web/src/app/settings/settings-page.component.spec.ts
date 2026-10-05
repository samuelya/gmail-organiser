import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { LlmModels } from '../core/llm.service';
import {
  GOOGLE_CONNECT_URL,
  GoogleAuthStatus,
  NAVIGATE_TO,
  SetupStatus,
} from '../setup/setup.service';
import { connectErrorMessage } from '../setup/steps/connect-gmail-step.component';
import { SettingsPage } from './settings-page.component';
import { AttachmentSettings, ClaudeSettings, SettingsDto } from './settings.models';

const EMAIL = 'user@example.com';
const URL_SAVED = 'http://ollama.example.com:11434';
const URL_NEW = 'http://ollama-2.example.com:11434';

const settings = (over: Partial<SettingsDto> = {}): SettingsDto => ({
  ollamaBaseUrl: URL_SAVED,
  chatModel: 'test-chat:1b',
  embeddingModel: null,
  actionLabelName: 'Example-Action',
  deleteLabelName: 'Example-Delete',
  documentTypeParent: null,
  setupWizardSeen: true,
  fetchChunkSize: 500,
  googleClient: {
    clientId: 'test-id.apps.googleusercontent.com',
    secretSet: true,
    lockedByEnv: false,
  },
  analysisDefaultCount: 20,
  analysisBodyMaxChars: 4000,
  analysisGroupingMode: 'auto',
  analysisRepresentativesPerGroup: 3,
  analysisMinGroupSize: 3,
  analysisDerivedConfidencePenalty: 0.1,
  analysisClusterDistance: 0.15,
  analysisMemoryShortCircuit: true,
  analysisMemoryMinApprovals: 3,
  bulkApproveThreshold: 0.8,
  autoArchiveOnActionDone: false,
  policyAutoApplyFetched: true,
  llmNumCtx: 8192,
  taxonomyMaxSenders: 80,
  taxonomyMaxLabels: 25,
  analysisPromptTemplate: null,
  ...over,
});

const googleStatus = (connected: boolean): GoogleAuthStatus => ({
  connected,
  reason: connected ? null : 'not_connected',
  accountEmail: connected ? EMAIL : null,
  scopes: [],
  missingScopes: [],
  redirectUri: 'http://localhost:4200/api/auth/google/callback',
});

const setupStatus: SetupStatus = {
  googleClientConfigured: true,
  gmailConnected: true,
  gmailReauthRequired: false,
  ollamaReachable: true,
  chatModelSelected: true,
  embeddingModelSelected: false,
  wizardSeen: true,
  complete: true,
};

const models: LlmModels = {
  reachable: true,
  version: '0.0.1-test',
  chatModels: [
    { name: 'test-chat:1b', sizeBytes: 1, family: 'test', parameterSize: '1B', capabilities: [] },
    { name: 'test-chat:2b', sizeBytes: 1, family: 'test', parameterSize: '2B', capabilities: [] },
  ],
  embeddingModels: [],
  error: null,
};

const VERSION = '9.8.7-test';
const PROMPT = 'Synthetic instructions.\n\n{{emails}}';
const defaultPrompt = { version: 'test-v1', template: PROMPT };
const appsScriptConfig = {
  scriptVersion: 1,
  config: 'const CONFIG = {};',
  generatedAt: '2026-01-01T00:00:00Z',
};

describe('SettingsPage', () => {
  let http: HttpTestingController;
  let connected: boolean;
  let snackOpen: ReturnType<typeof vi.fn>;
  let loaded: SettingsDto;

  /** Answers every pending GET the page makes, until none are left. */
  async function flushLoads(fixture: { whenStable: () => Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      const pending = http.match((r) => r.method === 'GET');
      if (pending.length === 0) return;
      for (const req of pending) {
        const path = req.request.url;
        if (path === '/api/settings') req.flush(loaded);
        else if (path === '/api/auth/google/status') req.flush(googleStatus(connected));
        else if (path === '/api/setup/status') req.flush(setupStatus);
        else if (path === '/api/llm/models') req.flush(models);
        else if (path === '/api/analysis/prompt/default') req.flush(defaultPrompt);
        else if (path === '/api/rules/apps-script/config') req.flush(appsScriptConfig);
        else if (path === '/healthz') req.flush({ status: 'ok', version: VERSION });
        else throw new Error(`unexpected GET ${path}`);
      }
    }
  }

  async function render(gmailConnected = true, dto = settings()) {
    configure(gmailConnected, dto);
    const fixture = TestBed.createComponent(SettingsPage);
    await flushLoads(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  /** Opens the page through the router, as the OAuth callback redirect does. */
  async function openAt(url: string, gmailConnected = true) {
    const navigate = vi.fn();
    configure(gmailConnected, settings(), navigate);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, SettingsPage);
    await flushLoads(harness.fixture);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { harness, el, q, navigate };
  }

  function configure(gmailConnected: boolean, dto: SettingsDto, navigate = vi.fn()) {
    connected = gmailConnected;
    loaded = dto;
    snackOpen = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'settings', component: SettingsPage }]),
        { provide: NAVIGATE_TO, useValue: navigate },
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: snackOpen } },
        // The dialog reports its result after the close animation.
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }

  const overlay = (id: string) =>
    document.querySelector<HTMLElement>(`.cdk-overlay-container [data-testid="${id}"]`);

  afterEach(() => {
    document.querySelector('.cdk-overlay-container')?.replaceChildren();
    http.verify();
  });

  it('renders the Google, Gmail connection and Ollama sections with the wizard steps', async () => {
    const { el, q } = await render();
    const headings = [...el.querySelectorAll('h2')].map((h) => h.textContent?.trim());
    expect(headings).toEqual([
      'Google',
      'Gmail connection',
      'Ollama',
      'Fetch',
      'Labels',
      'Analysis',
      'Apps Script',
      'Data',
    ]);
    expect(q('section-labels')!.querySelector('app-labels-settings')).not.toBeNull();
    expect(q('section-data')!.querySelector('app-data-settings')).not.toBeNull();
    expect(q('section-google')!.querySelector('app-google-client-step')).not.toBeNull();
    expect(q('section-gmail')!.querySelector('app-connect-gmail-step')).not.toBeNull();
    expect(q('section-ollama')!.querySelector('app-ollama-url-step')).not.toBeNull();
    expect(q('section-ollama')!.querySelector('app-models-step')).not.toBeNull();
    expect(q('connected-as')!.textContent).toContain(EMAIL);
    expect(q('no-client')).toBeNull();
  });

  it('shows the api version from /healthz in the footer', async () => {
    const { q } = await render();
    expect(q('app-version')!.textContent!.trim()).toBe(`Gmail Organiser v${VERSION}`);
  });

  it('callback connected: shows the result on the Gmail card, scrolls to it, clears the params', async () => {
    const original = Element.prototype.scrollIntoView;
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    try {
      const { el, q } = await openAt('/settings?gmail=connected');
      expect(q('connect-success')!.textContent).toContain(`Gmail connected as ${EMAIL}.`);
      expect(TestBed.inject(Router).url).toBe('/settings');
      expect(scroll).toHaveBeenCalledTimes(1);
      expect(scroll.mock.contexts[0]).toBe(el.querySelector('[data-testid="section-gmail"]'));
    } finally {
      Element.prototype.scrollIntoView = original;
    }
  });

  it('callback error: shows the mapped message and clears the params', async () => {
    const { q } = await openAt('/settings?gmail=error&reason=state_mismatch', false);
    expect(q('connect-error')!.textContent).toContain(connectErrorMessage('state_mismatch'));
    expect(TestBed.inject(Router).url).toBe('/settings');
  });

  it('no callback params: no result, URL untouched', async () => {
    const { q } = await openAt('/settings');
    expect(q('connect-success')).toBeNull();
    expect(q('connect-error')).toBeNull();
    expect(q('connected-as')).not.toBeNull();
  });

  it('Reconnect returns to Settings', async () => {
    const { q, navigate } = await openAt('/settings');
    q('reconnect')!.click();
    expect(navigate).toHaveBeenCalledWith(`${GOOGLE_CONNECT_URL}?returnTo=settings`);
  });

  it('Disconnect asks first; Cancel keeps the connection', async () => {
    const { fixture, q } = await render();
    q('disconnect')!.click();
    await fixture.whenStable();
    expect(overlay('confirm-ok')!.textContent).toContain('Disconnect');

    overlay('confirm-cancel')!.click();
    await flushLoads(fixture);
    http.expectNone('/api/auth/google/disconnect');
    expect(overlay('confirm-ok')).toBeNull();
    expect(q('connected-as')).not.toBeNull();
  });

  it('Disconnect confirmed: posts, then shows the not-connected state', async () => {
    const { fixture, q } = await render();
    q('disconnect')!.click();
    await fixture.whenStable();
    overlay('confirm-ok')!.click();
    await fixture.whenStable();

    connected = false;
    http.expectOne({ method: 'POST', url: '/api/auth/google/disconnect' }).flush(null);
    await flushLoads(fixture);
    expect(q('connected-as')).toBeNull();
    expect(q('connect')).not.toBeNull();
  });

  it('saving the Ollama URL sends only the URL and keeps an unsaved model choice', async () => {
    const { fixture, q } = await render();
    const page = fixture.debugElement;
    const modelsStep = page.query((d) => d.name === 'app-models-step').componentInstance;
    modelsStep.form.controls.chatModel.setValue('test-chat:2b');

    const url = q('ollama-url') as HTMLInputElement;
    url.value = URL_NEW;
    url.dispatchEvent(new Event('input'));
    q('save-url')!.click();
    await fixture.whenStable();

    const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(save.request.body).toEqual({ ollamaBaseUrl: URL_NEW });
    save.flush(settings({ ollamaBaseUrl: URL_NEW }));
    await fixture.whenStable();

    const list = http.expectOne((r) => r.url === '/api/llm/models');
    expect(list.request.params.get('baseUrl')).toBe(URL_NEW);
    list.flush(models);
    await flushLoads(fixture);

    expect(snackOpen).toHaveBeenCalledWith('Ollama URL saved', undefined, { duration: 3000 });
    expect(modelsStep.form.controls.chatModel.value).toBe('test-chat:2b');
  });

  describe('fetch chunk size', () => {
    async function setChunk(
      fixture: { whenStable: () => Promise<unknown> },
      input: HTMLInputElement,
      value: string,
    ) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new Event('blur'));
      await fixture.whenStable();
    }

    it('loads the saved value', async () => {
      const { q } = await render();
      expect((q('fetch-chunk-size') as HTMLInputElement).value).toBe('500');
    });

    it.each([
      ['', 'The fetch chunk size is required.'],
      ['5', 'Enter a number from 10 to 5000.'],
      ['5100', 'Enter a number from 10 to 5000.'],
      ['150.5', 'Enter a whole number.'],
    ])('rejects %j without saving', async (value, message) => {
      const { fixture, q } = await render();
      await setChunk(fixture, q('fetch-chunk-size') as HTMLInputElement, value);
      q('save-fetch')!.click();
      await fixture.whenStable();

      http.expectNone({ method: 'PUT', url: '/api/settings' });
      expect(q('section-fetch')!.querySelector('mat-error')?.textContent?.trim()).toBe(message);
    });

    it('saves only the chunk size', async () => {
      const { fixture, q } = await render();
      await setChunk(fixture, q('fetch-chunk-size') as HTMLInputElement, '1250');
      q('save-fetch')!.click();
      await fixture.whenStable();

      const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
      expect(save.request.body).toEqual({ fetchChunkSize: 1250 });
      save.flush(settings({ fetchChunkSize: 1250 }));
      await flushLoads(fixture);
      expect(snackOpen).toHaveBeenCalledWith('Fetch chunk size saved', undefined, {
        duration: 3000,
      });
    });
  });

  describe('analysis', () => {
    it('opens the prompt field pre-filled with the built-in prompt', async () => {
      const { q } = await render();
      expect((q('analysisPromptTemplate') as HTMLTextAreaElement).value).toBe(PROMPT);
      expect(q('prompt-source')!.textContent!.trim()).toBe('Built-in prompt (test-v1)');
    });

    it('Reset to default restores the built-in text without fetching the prompt again', async () => {
      const { fixture, q } = await render();
      const prompt = q('analysisPromptTemplate') as HTMLTextAreaElement;
      prompt.value = 'Custom {{emails}}';
      prompt.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      expect(q('prompt-source')!.textContent!.trim()).toBe('Custom prompt');

      q('reset-prompt')!.click();
      await fixture.whenStable();
      http.expectNone({ method: 'GET', url: '/api/analysis/prompt/default' });
      expect(prompt.value).toBe(PROMPT);
    });

    it('saves only the changed fields and shows server field errors inline', async () => {
      const { fixture, q } = await render();
      const count = q('analysisDefaultCount') as HTMLInputElement;
      count.value = '50';
      count.dispatchEvent(new Event('input'));
      q('save-analysis')!.click();
      await fixture.whenStable();

      const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
      expect(save.request.body).toEqual({ analysisDefaultCount: 50 });
      save.flush(
        { errors: { analysisDefaultCount: ['Synthetic server error.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      await fixture.whenStable();
      expect(q('section-analysis')!.querySelector('mat-error')?.textContent?.trim()).toBe(
        'Synthetic server error.',
      );
    });

    it('updates the form from the saved settings', async () => {
      const { fixture, q } = await render();
      const toggle = q('autoArchiveOnActionDone')!.querySelector('button')!;
      toggle.click();
      q('save-analysis')!.click();
      await fixture.whenStable();

      const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
      expect(save.request.body).toEqual({ autoArchiveOnActionDone: true });
      save.flush(settings({ autoArchiveOnActionDone: true }));
      await fixture.whenStable();
      expect(snackOpen).toHaveBeenCalledWith('Analysis settings saved', undefined, {
        duration: 3000,
      });
      const section = fixture.debugElement.query((d) => d.name === 'app-analysis-settings');
      expect(section.componentInstance.changes()).toEqual({});
    });
  });

  describe('attachments', () => {
    const attachments: AttachmentSettings = {
      enabled: true,
      types: [
        { type: 'pdf', enabled: true },
        { type: 'image', enabled: true },
        { type: 'archive', enabled: false },
      ],
      maxBytes: 10 * 1024 * 1024,
      maxImageBytes: 10 * 1024 * 1024,
      maxChars: 4000,
      maxPerMessage: 5,
    };

    it('is hidden when the API has no attachments block', async () => {
      const { q } = await render();
      expect(q('section-attachments')).toBeNull();
    });

    it('saves only the changed attachment fields and updates from the response', async () => {
      const { fixture, q } = await render(true, settings({ attachments }));
      expect(q('section-attachments')).not.toBeNull();
      q('type-pdf')!.querySelector('button')!.click();
      q('save-attachments')!.click();
      await fixture.whenStable();

      const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
      expect(save.request.body).toEqual({
        attachments: { types: [{ type: 'pdf', enabled: false }] },
      });
      save.flush(
        settings({ attachments: { ...attachments, types: [{ type: 'pdf', enabled: false }] } }),
      );
      await fixture.whenStable();
      expect(snackOpen).toHaveBeenCalledWith('Attachment settings saved', undefined, {
        duration: 3000,
      });
      const section = fixture.debugElement.query((d) => d.name === 'app-attachment-settings');
      expect(section.componentInstance.changes()).toEqual({});
    });
  });

  describe('claude review', () => {
    const claude: ClaudeSettings = {
      claudeReviewerMode: 'off',
      claudeSuggestLowConfidence: false,
      claudeSuggestThreshold: 0.6,
      claudeSuggestNewLabels: false,
      claudeRunTimeoutSeconds: 600,
      claudeMaxItemsPerRun: 10,
      claudeMaxTurns: 80,
      claudeModel: null,
      claudeTokenSet: false,
    };

    it('shows the Apps Script section with the generated config', async () => {
      const { q } = await render();
      expect(q('section-apps-script')).not.toBeNull();
      expect(q('config-block')?.textContent).toBe(appsScriptConfig.config);
    });

    it('is hidden when the API has no Claude fields', async () => {
      const { q } = await render();
      expect(q('section-claude')).toBeNull();
    });

    it('saves only the changed mode and updates from the response', async () => {
      const { fixture, q } = await render(true, settings(claude));
      expect(q('section-claude')).not.toBeNull();
      q('claude-mode-headless_claude_code')!.querySelector('input')!.click();
      await fixture.whenStable();
      q('save-claude')!.click();
      await fixture.whenStable();

      const save = http.expectOne({ method: 'PUT', url: '/api/settings' });
      expect(save.request.body).toEqual({ claudeReviewerMode: 'headless_claude_code' });
      save.flush(settings({ ...claude, claudeReviewerMode: 'headless_claude_code' }));
      await fixture.whenStable();
      expect(snackOpen).toHaveBeenCalledWith('Claude review settings saved', undefined, {
        duration: 3000,
      });
      const section = fixture.debugElement.query((d) => d.name === 'app-claude-settings');
      expect(section.componentInstance.changes()).toEqual({});
    });
  });
});
