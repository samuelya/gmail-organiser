import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { LlmModels } from '../core/llm.service';
import { GoogleAuthStatus, SetupStatus } from '../setup/setup.service';
import { SettingsPage } from './settings-page.component';
import { SettingsDto } from './settings.models';

const EMAIL = 'user@example.com';
const URL_SAVED = 'http://ollama.example.com:11434';
const URL_NEW = 'http://ollama-2.example.com:11434';

const settings = (over: Partial<SettingsDto> = {}): SettingsDto => ({
  ollamaBaseUrl: URL_SAVED,
  chatModel: 'test-chat:1b',
  embeddingModel: null,
  actionLabelName: 'Example-Action',
  deleteLabelName: 'Example-Delete',
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

describe('SettingsPage', () => {
  let http: HttpTestingController;
  let connected: boolean;
  let snackOpen: ReturnType<typeof vi.fn>;

  /** Answers every pending GET the page makes, until none are left. */
  async function flushLoads(fixture: { whenStable: () => Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      const pending = http.match((r) => r.method === 'GET');
      if (pending.length === 0) return;
      for (const req of pending) {
        const path = req.request.url;
        if (path === '/api/settings') req.flush(settings());
        else if (path === '/api/auth/google/status') req.flush(googleStatus(connected));
        else if (path === '/api/setup/status') req.flush(setupStatus);
        else if (path === '/api/llm/models') req.flush(models);
        else throw new Error(`unexpected GET ${path}`);
      }
    }
  }

  async function render(gmailConnected = true) {
    connected = gmailConnected;
    snackOpen = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: snackOpen } },
        // The dialog reports its result after the close animation.
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(SettingsPage);
    await flushLoads(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
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
    expect(headings).toEqual(['Google', 'Gmail connection', 'Ollama', 'Fetch', 'Analysis']);
    expect(q('section-google')!.querySelector('app-google-client-step')).not.toBeNull();
    expect(q('section-gmail')!.querySelector('app-connect-gmail-step')).not.toBeNull();
    expect(q('section-ollama')!.querySelector('app-ollama-url-step')).not.toBeNull();
    expect(q('section-ollama')!.querySelector('app-models-step')).not.toBeNull();
    expect(q('connected-as')!.textContent).toContain(EMAIL);
    expect(q('no-client')).toBeNull();
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
    const PROMPT = 'Synthetic instructions.\n\n{{emails}}';

    it('Reset to default clears the field and previews the built-in prompt', async () => {
      const { fixture, q } = await render();
      q('reset-prompt')!.click();
      await fixture.whenStable();

      http
        .expectOne({ method: 'GET', url: '/api/analysis/prompt/default' })
        .flush({ version: 'test-v1', template: PROMPT });
      await fixture.whenStable();
      expect((q('analysisPromptTemplate') as HTMLTextAreaElement).value).toBe('');
      expect(q('default-prompt-preview')!.textContent).toBe(PROMPT);
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
});
