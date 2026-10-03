import { Clipboard } from '@angular/cdk/clipboard';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ClaudeTestResult, McpConfig } from '../core/claude.models';
import { ClaudeSettingsSection } from './claude-settings.component';
import { ClaudeSettings, ClaudeSettingsUpdate, CLAUDE_TOKEN_STATUS } from './settings.models';

const claude = (over: Partial<ClaudeSettings> = {}): ClaudeSettings => ({
  claudeReviewerMode: 'off',
  claudeSuggestLowConfidence: false,
  claudeSuggestThreshold: 0.6,
  claudeSuggestNewLabels: false,
  claudeRunTimeoutSeconds: 600,
  claudeMaxItemsPerRun: 10,
  claudeMaxTurns: 80,
  claudeModel: null,
  claudeTokenSet: false,
  ...over,
});

const config = (token: string): McpConfig => ({
  endpointUrl: 'http://localhost.example.com/mcp',
  claudeDesktopSnippet: `{ "mcpServers": { "test": { "args": ["Bearer ${token}"] } } }`,
});

const testResult = (over: Partial<ClaudeTestResult> = {}): ClaudeTestResult => ({
  ok: true,
  mode: 'headless_claude_code',
  cliVersion: '0.0.1-test',
  tokenSet: true,
  mcpReachable: true,
  elapsedMs: 1500,
  error: null,
  ...over,
});

describe('ClaudeSettingsSection', () => {
  let fixture: ComponentFixture<ClaudeSettingsSection>;
  let section: ClaudeSettingsSection;
  let emitted: ClaudeSettingsUpdate[];
  let el: HTMLElement;
  let http: HttpTestingController;
  let copy: ReturnType<typeof vi.fn>;
  let snackOpen: ReturnType<typeof vi.fn>;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const overlay = (id: string) =>
    document.querySelector<HTMLElement>(`.cdk-overlay-container [data-testid="${id}"]`);

  async function render(settings: ClaudeSettings | null = claude()) {
    copy = vi.fn(() => true);
    snackOpen = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Clipboard, useValue: { copy } },
        { provide: MatSnackBar, useValue: { open: snackOpen } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ClaudeSettingsSection);
    section = fixture.componentInstance;
    emitted = [];
    section.changed.subscribe((c) => emitted.push(c));
    fixture.componentRef.setInput('settings', settings);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  async function chooseMode(mode: string) {
    q(`claude-mode-${mode}`)!.querySelector('input')!.click();
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function flushSnippet(token = 'test-token-1') {
    http.expectOne({ method: 'GET', url: '/api/claude/mcp-config' }).flush(config(token));
    await fixture.whenStable();
  }

  afterEach(() => {
    document.querySelector('.cdk-overlay-container')?.replaceChildren();
    http.verify();
  });

  it('is disabled until the settings load', async () => {
    await render(null);
    expect(section.form.disabled).toBe(true);
  });

  it('Off shows only the mode choice', async () => {
    await render();
    expect(q('claude-headless')).toBeNull();
    expect(q('claude-desktop')).toBeNull();
    expect(q('claude-suggest')).toBeNull();
    expect(q('claude-test')).toBeNull();
  });

  it('headless shows the limits and the token warning, and hides the desktop fields', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
    expect(q('claude-headless')).not.toBeNull();
    expect(q('claudeMaxTurns')).not.toBeNull();
    expect(q('claude-suggest')).not.toBeNull();
    expect(q('claude-desktop')).toBeNull();
    expect(q('claude-token-missing')!.textContent).toContain(CLAUDE_TOKEN_STATUS.missing);
  });

  it('headless with the token set shows the set chip', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code', claudeTokenSet: true }));
    expect(q('claude-token-set')!.textContent).toContain(CLAUDE_TOKEN_STATUS.set);
    expect(q('claude-token-missing')).toBeNull();
  });

  it('switching to desktop loads the snippet once into a read-only textarea', async () => {
    await render();
    await chooseMode('claude_desktop');
    await flushSnippet();
    const snippet = q<HTMLTextAreaElement>('claude-snippet')!;
    expect(snippet.readOnly).toBe(true);
    expect(snippet.value).toContain('test-token-1');
    expect(q('claude-headless')).toBeNull();

    await chooseMode('off');
    await chooseMode('claude_desktop');
    http.expectNone('/api/claude/mcp-config');
  });

  it('saves only the changed fields; hidden fields are not sent', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
    await type('claudeMaxTurns', '120');
    await type('claudeModel', ' test-model ');
    q<HTMLElement>('claude-low')!.querySelector('button')!.click();
    await fixture.whenStable();
    q('save-claude')!.click();
    expect(emitted).toEqual([
      { claudeSuggestLowConfidence: true, claudeMaxTurns: 120, claudeModel: 'test-model' },
    ]);

    await chooseMode('off');
    q('save-claude')!.click();
    expect(emitted[1]).toEqual({ claudeReviewerMode: 'off' });
  });

  it('an emptied model clears it', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code', claudeModel: 'test-model' }));
    await type('claudeModel', '');
    q('save-claude')!.click();
    expect(emitted).toEqual([{ claudeModel: '' }]);
  });

  it('does not save an out-of-range limit', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
    await type('claudeRunTimeoutSeconds', '30');
    q('save-claude')!.click();
    await fixture.whenStable();
    expect(emitted).toEqual([]);
    expect(el.textContent).toContain('Enter a number from 60 to 3600.');
  });

  it('shows server field errors inline', async () => {
    await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
    fixture.componentRef.setInput('serverErrors', { claudeMaxTurns: ['Too many turns.'] });
    await fixture.whenStable();
    expect(el.textContent).toContain('Too many turns.');
  });

  describe('test connection', () => {
    it('waits for an unsaved mode change', async () => {
      await render();
      await chooseMode('headless_claude_code');
      expect(q<HTMLButtonElement>('claude-test')!.disabled).toBe(true);
      expect(q('claude-test-save-first')).not.toBeNull();
    });

    it('shows a passing headless test with the details', async () => {
      await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
      q('claude-test')!.click();
      await fixture.whenStable();
      expect(q('claude-cancel-test')).not.toBeNull();
      http.expectOne({ method: 'POST', url: '/api/claude/test' }).flush(testResult());
      await fixture.whenStable();
      expect(q('claude-test-ok')!.textContent).toContain('1.5 s');
      expect(q('claude-test-details')!.textContent).toContain('0.0.1-test');
    });

    it('shows the error text verbatim', async () => {
      await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
      q('claude-test')!.click();
      const error = 'Claude Code CLI not found: rebuild with WITH_CLAUDE=true.';
      http.expectOne('/api/claude/test').flush(testResult({ ok: false, cliVersion: null, error }));
      await fixture.whenStable();
      expect(q('claude-test-error')!.textContent!.trim()).toBe(`error ${error}`);
      expect(q('claude-test-details')!.textContent).toContain('not found');
    });

    it('a desktop test lists only the MCP endpoint', async () => {
      await render(claude({ claudeReviewerMode: 'claude_desktop' }));
      await flushSnippet();
      q('claude-test')!.click();
      http
        .expectOne('/api/claude/test')
        .flush(testResult({ mode: 'claude_desktop', cliVersion: null, mcpReachable: false }));
      await fixture.whenStable();
      const details = q('claude-test-details')!.textContent!;
      expect(details).toContain('not reachable');
      expect(details).not.toContain('CLI');
    });

    it('a mode change cancels a running test', async () => {
      await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
      q('claude-test')!.click();
      const req = http.expectOne('/api/claude/test');
      await chooseMode('claude_desktop');
      await flushSnippet();
      expect(req.cancelled).toBe(true);
      expect(section.testRun()).toEqual({ state: 'idle' });
    });

    it('shows a failed request', async () => {
      await render(claude({ claudeReviewerMode: 'headless_claude_code' }));
      q('claude-test')!.click();
      http.expectOne('/api/claude/test').flush(null, { status: 500, statusText: 'Server Error' });
      await fixture.whenStable();
      expect(q('claude-test-failed')).not.toBeNull();
    });
  });

  describe('desktop actions', () => {
    beforeEach(async () => {
      await render(claude({ claudeReviewerMode: 'claude_desktop' }));
      await flushSnippet();
    });

    it('copies the snippet', () => {
      q('claude-copy-snippet')!.click();
      expect(copy).toHaveBeenCalledWith(config('test-token-1').claudeDesktopSnippet);
      expect(snackOpen).toHaveBeenCalled();
    });

    describe('copy prompt', () => {
      let write: ReturnType<typeof vi.fn>;
      const flushPrompt = () =>
        http.expectOne('/api/claude/prompts/review-pending').flush('Synthetic review prompt.');
      const settle = () => new Promise((resolve) => setTimeout(resolve));

      function stubClipboard(result: Promise<void>) {
        write = vi.fn((items: { data: Record<string, Promise<Blob>> }[]) =>
          items[0].data['text/plain'].then(() => result),
        );
        vi.stubGlobal(
          'ClipboardItem',
          class {
            constructor(public data: Record<string, Promise<Blob>>) {}
          },
        );
        vi.stubGlobal('navigator', { clipboard: { write } });
      }

      afterEach(() => vi.unstubAllGlobals());

      it('starts the clipboard write in the click, before the prompt arrives', async () => {
        stubClipboard(Promise.resolve());
        q('claude-copy-prompt')!.click();
        expect(write).toHaveBeenCalledTimes(1);
        const req = http.expectOne('/api/claude/prompts/review-pending');
        expect(req.request.responseType).toBe('text');
        req.flush('Synthetic review prompt.');
        const blob: Blob = await write.mock.calls[0][0][0].data['text/plain'];
        expect(await blob.text()).toBe('Synthetic review prompt.');
        await settle();
        expect(snackOpen).toHaveBeenCalledWith('Review prompt copied', undefined, {
          duration: 2000,
        });
      });

      it('shows an error when the browser refuses the write', async () => {
        stubClipboard(Promise.reject(new DOMException('denied', 'NotAllowedError')));
        q('claude-copy-prompt')!.click();
        flushPrompt();
        await settle();
        expect(snackOpen).toHaveBeenCalledWith('Could not copy the review prompt', 'Close');
      });

      it('falls back to the CDK clipboard and shows an error when it fails', async () => {
        vi.stubGlobal('ClipboardItem', undefined);
        copy.mockReturnValue(false);
        q('claude-copy-prompt')!.click();
        flushPrompt();
        await settle();
        expect(copy).toHaveBeenCalledWith('Synthetic review prompt.');
        expect(snackOpen).toHaveBeenCalledWith('Could not copy the review prompt', 'Close');
      });

      it('leaves a failed request to the error interceptor', async () => {
        stubClipboard(Promise.resolve());
        q('claude-copy-prompt')!.click();
        http
          .expectOne('/api/claude/prompts/review-pending')
          .flush(null, { status: 500, statusText: 'Server Error' });
        await settle();
        expect(snackOpen).not.toHaveBeenCalled();
        expect(section.copyingPrompt()).toBe(false);
      });
    });

    it('rotate asks first; Cancel keeps the token', async () => {
      q('claude-rotate-token')!.click();
      await fixture.whenStable();
      overlay('confirm-cancel')!.click();
      await fixture.whenStable();
      http.expectNone('/api/claude/mcp-token/rotate');
    });

    it('rotate confirmed: posts and shows the new snippet', async () => {
      q('claude-rotate-token')!.click();
      await fixture.whenStable();
      overlay('confirm-ok')!.click();
      await fixture.whenStable();
      http
        .expectOne({ method: 'POST', url: '/api/claude/mcp-token/rotate' })
        .flush(config('test-token-2'));
      await fixture.whenStable();
      expect(q<HTMLTextAreaElement>('claude-snippet')!.value).toContain('test-token-2');
    });
  });
});
