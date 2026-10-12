import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { JobsService } from '../core/jobs.service';
import { LlmModels } from '../core/llm.service';
import { GoogleAuthStatus, NAVIGATE_TO, SetupStatus } from '../setup/setup.service';
import { SettingsPage } from './settings-page.component';
import { SettingsDto } from './settings.models';

/** Synthetic fixtures and the TestBed set-up shared by the Settings page specs. */
export const EMAIL = 'user@example.com';
export const URL_SAVED = 'http://ollama.example.com:11434';
export const URL_NEW = 'http://ollama-2.example.com:11434';

export const settings = (over: Partial<SettingsDto> = {}): SettingsDto => ({
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
  llmProvider: 'ollama',
  claudeApiModel: null,
  claudeApiKeySet: false,
  claudeApiKeyHint: null,
  ...over,
});

export const googleStatus = (connected: boolean): GoogleAuthStatus => ({
  connected,
  reason: connected ? null : 'not_connected',
  accountEmail: connected ? EMAIL : null,
  scopes: [],
  missingScopes: [],
  redirectUri: 'http://localhost:4200/api/auth/google/callback',
});

export const setupStatus: SetupStatus = {
  googleClientConfigured: true,
  gmailConnected: true,
  gmailReauthRequired: false,
  ollamaReachable: true,
  chatModelSelected: true,
  embeddingModelSelected: false,
  wizardSeen: true,
  complete: true,
  completedOnce: true,
};

export const models: LlmModels = {
  reachable: true,
  version: '0.0.1-test',
  chatModels: [
    { name: 'test-chat:1b', sizeBytes: 1, family: 'test', parameterSize: '1B', capabilities: [] },
    { name: 'test-chat:2b', sizeBytes: 1, family: 'test', parameterSize: '2B', capabilities: [] },
  ],
  embeddingModels: [],
  error: null,
};

export const VERSION = '9.8.7-test';
export const PROMPT = 'Synthetic instructions.\n\n{{emails}}';
export const defaultPrompt = { version: 'test-v1', template: PROMPT };
export const appsScriptConfig = {
  scriptVersion: 1,
  config: 'const CONFIG = {};',
  generatedAt: '2026-01-01T00:00:00Z',
};

export const retentionStatus = {
  enabled: false,
  lastRunAt: null,
  lastMarked: null,
  nextDueAt: null,
  eligibleNow: 0,
};

/** Every optional block present, so every card renders. */
export const fullSettings = () =>
  settings({
    claudeReviewerMode: 'off',
    claudeSuggestLowConfidence: false,
    claudeSuggestThreshold: 0.6,
    claudeSuggestNewLabels: false,
    claudeRunTimeoutSeconds: 600,
    claudeMaxItemsPerRun: 10,
    claudeMaxTurns: 80,
    claudeModel: null,
    claudeTokenSet: false,
    protection: {
      attachments: true,
      starred: true,
      important: true,
      repliedThreads: true,
      allowlistedDomains: [],
    },
    attachments: {
      enabled: false,
      types: [],
      maxBytes: 1024,
      maxImageBytes: 1024,
      maxChars: 1000,
      maxPerMessage: 1,
    },
  });

/**
 * Renders the page against `HttpTestingController` and answers its loads. One per test: the spec
 * passes in its own snack bar and navigation mocks.
 */
export class SettingsPageTest {
  http!: HttpTestingController;
  /** What `/api/auth/google/status` answers; a test flips it to see the page reload. */
  connected = true;
  private loaded = settings();

  /** `snackOpen` stands in for `MatSnackBar.open`, `navigate` for `NAVIGATE_TO`. */
  constructor(
    private readonly snackOpen: unknown,
    readonly navigate: unknown,
  ) {}

  /** Answers every pending GET the page makes, until none are left. */
  async flushLoads(fixture: { whenStable: () => Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      const pending = this.http.match((r) => r.method === 'GET');
      if (pending.length === 0) return;
      for (const req of pending) {
        const path = req.request.url;
        if (path === '/api/settings') req.flush(this.loaded);
        else if (path === '/api/auth/google/status') req.flush(googleStatus(this.connected));
        else if (path === '/api/setup/status') req.flush(setupStatus);
        else if (path === '/api/llm/models') req.flush(models);
        else if (path === '/api/analysis/prompt/default') req.flush(defaultPrompt);
        else if (path === '/api/rules/apps-script/config') req.flush(appsScriptConfig);
        else if (path === '/api/clean-up/retention') req.flush(retentionStatus);
        else if (path === '/api/senders') req.flush({ items: [], page: 1, pageSize: 50, total: 0 });
        else if (path === '/healthz') req.flush({ status: 'ok', version: VERSION });
        else throw new Error(`unexpected GET ${path}`);
      }
    }
  }

  async render(gmailConnected = true, dto = settings()) {
    this.configure(gmailConnected, dto);
    const fixture = TestBed.createComponent(SettingsPage);
    await this.flushLoads(fixture);
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, q: query(el) };
  }

  /** Opens the page through the router, as the OAuth callback redirect and deep links do. */
  async openAt(url: string, gmailConnected = true, dto = settings()) {
    this.configure(gmailConnected, dto);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, SettingsPage);
    await this.flushLoads(harness.fixture);
    const el = harness.routeNativeElement as HTMLElement;
    return { harness, el, q: query(el), navigate: this.navigate };
  }

  /** Call from `afterEach`. */
  verify() {
    document.querySelector('.cdk-overlay-container')?.replaceChildren();
    this.http.verify();
  }

  private configure(gmailConnected: boolean, dto: SettingsDto) {
    this.connected = gmailConnected;
    this.loaded = dto;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'settings', component: SettingsPage }]),
        { provide: NAVIGATE_TO, useValue: this.navigate },
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: this.snackOpen } },
        {
          provide: JobsService,
          useValue: { activeJobs: signal([]), reconnects: signal(0), job: () => undefined },
        },
        // The dialog reports its result after the close animation.
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    this.http = TestBed.inject(HttpTestingController);
  }
}

const query = (el: HTMLElement) => (id: string) =>
  el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
