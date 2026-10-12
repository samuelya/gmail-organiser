import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { GOOGLE_CONNECT_URL } from '../setup/setup.service';
import { connectErrorMessage } from '../setup/steps/connect-gmail-step.component';
import { AttachmentSettings, ClaudeSettings } from './settings.models';
import {
  appsScriptConfig,
  EMAIL,
  models,
  PROMPT,
  settings,
  SettingsPageTest,
  URL_NEW,
  VERSION,
} from './settings-page.testing';

describe('SettingsPage', () => {
  let ctx: SettingsPageTest;
  let snackOpen: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    snackOpen = vi.fn();
    ctx = new SettingsPageTest(snackOpen, vi.fn());
  });
  afterEach(() => ctx.verify());

  const render = (...args: Parameters<SettingsPageTest['render']>) => ctx.render(...args);
  const openAt = (...args: Parameters<SettingsPageTest['openAt']>) => ctx.openAt(...args);
  const flushLoads = (fixture: { whenStable: () => Promise<unknown> }) => ctx.flushLoads(fixture);
  const overlay = (id: string) =>
    document.querySelector<HTMLElement>(`.cdk-overlay-container [data-testid="${id}"]`);

  it('renders the Google, Gmail connection and Ollama sections with the wizard steps', async () => {
    const { el, q } = await render();
    const headings = [...el.querySelectorAll('h2')].map((h) => h.textContent?.trim());
    expect(headings).toEqual([
      'Setup',
      'Google',
      'Gmail connection',
      'Analysis model provider',
      'Ollama',
      'Fetch',
      'Labels',
      'Analysis',
      'Taxonomy',
      'Apps Script',
      'Retention',
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

  it('the Run setup again link points to /setup', async () => {
    const { q } = await render();
    expect(q('rerun-setup')!.getAttribute('href')).toBe('/setup');
    expect(q('rerun-setup')!.textContent).toContain('Run setup again');
  });

  it('shows the api version from /healthz in the footer', async () => {
    const { q } = await render();
    expect(q('app-version')!.textContent!.trim()).toBe(`Gmail Organiser v${VERSION}`);
  });

  it('jumping to a card focuses its heading and leaves no tabindex behind', async () => {
    const { fixture, el } = await render();
    fixture.componentInstance.jumpToSection('settings-llm-provider');
    const heading = el.querySelector<HTMLElement>('#settings-llm-provider')!;
    expect(document.activeElement).toBe(heading);
    heading.blur();
    expect(heading.hasAttribute('tabindex')).toBe(false);
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
    ctx.http.expectNone('/api/auth/google/disconnect');
    expect(overlay('confirm-ok')).toBeNull();
    expect(q('connected-as')).not.toBeNull();
  });

  it('Disconnect confirmed: posts, then shows the not-connected state', async () => {
    const { fixture, q } = await render();
    q('disconnect')!.click();
    await fixture.whenStable();
    overlay('confirm-ok')!.click();
    await fixture.whenStable();

    ctx.connected = false;
    ctx.http.expectOne({ method: 'POST', url: '/api/auth/google/disconnect' }).flush(null);
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

    const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
    expect(save.request.body).toEqual({ ollamaBaseUrl: URL_NEW });
    save.flush(settings({ ollamaBaseUrl: URL_NEW }));
    await fixture.whenStable();

    // The models step and the triage model each list against the new URL.
    const lists = ctx.http.match((r) => r.url === '/api/llm/models');
    expect(lists.map((l) => l.request.params.get('baseUrl'))).toEqual([URL_NEW, URL_NEW]);
    lists.forEach((l) => l.flush(models));
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

      ctx.http.expectNone({ method: 'PUT', url: '/api/settings' });
      expect(q('section-fetch')!.querySelector('mat-error')?.textContent?.trim()).toBe(message);
    });

    it('saves only the chunk size', async () => {
      const { fixture, q } = await render();
      await setChunk(fixture, q('fetch-chunk-size') as HTMLInputElement, '1250');
      q('save-fetch')!.click();
      await fixture.whenStable();

      const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
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
      ctx.http.expectNone({ method: 'GET', url: '/api/analysis/prompt/default' });
      expect(prompt.value).toBe(PROMPT);
    });

    it('saves only the changed fields and shows server field errors inline', async () => {
      const { fixture, q } = await render();
      const count = q('analysisDefaultCount') as HTMLInputElement;
      count.value = '50';
      count.dispatchEvent(new Event('input'));
      q('save-analysis')!.click();
      await fixture.whenStable();

      const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
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

      const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
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

      const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
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

      const save = ctx.http.expectOne({ method: 'PUT', url: '/api/settings' });
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
