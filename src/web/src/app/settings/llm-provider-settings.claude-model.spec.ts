import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ClaudeApiModels } from '../core/llm.service';
import { LlmProviderSettings } from './llm-provider.models';
import { LlmProviderSettingsSection } from './llm-provider-settings.component';
import { SettingsDto } from './settings.models';

const settingsOf = (over: Partial<LlmProviderSettings> = {}): SettingsDto =>
  ({
    llmProvider: 'claude_api',
    claudeApiModel: null,
    claudeApiKeySet: true,
    claudeApiKeyHint: '…abcd',
    ...over,
  }) as SettingsDto;

const listOf = (over: Partial<ClaudeApiModels> = {}): ClaudeApiModels => ({
  keySet: true,
  reachable: true,
  error: null,
  models: [
    { id: 'test-model-b', displayName: 'Test Model B', createdAt: '2026-02-01T00:00:00Z' },
    { id: 'test-model-a', displayName: 'Test Model A', createdAt: null },
  ],
  ...over,
});

describe('LlmProviderSettingsSection Claude model', () => {
  let fixture: ComponentFixture<LlmProviderSettingsSection>;
  let el: HTMLElement;
  let http: HttpTestingController;
  let emitted: SettingsDto[];
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const modelsUrl = '/api/llm/claude-api/models';
  const testUrl = '/api/llm/claude-api/test-model';
  const section = () => fixture.componentInstance;

  async function render(settings: SettingsDto) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LlmProviderSettingsSection);
    el = fixture.nativeElement as HTMLElement;
    emitted = [];
    fixture.componentInstance.saved.subscribe((s) => {
      emitted.push(s);
      fixture.componentRef.setInput('settings', s);
    });
    fixture.componentRef.setInput('settings', settings);
    await fixture.whenStable();
  }

  async function flush(list: ClaudeApiModels = listOf()) {
    http.expectOne(modelsUrl).flush(list);
    await fixture.whenStable();
  }

  async function choose(model: string) {
    section().model.setValue(model);
    await fixture.whenStable();
  }

  async function click(id: string) {
    q<HTMLButtonElement>(id)!.click();
    await fixture.whenStable();
  }

  afterEach(() => http.verify());

  it('lists the models as returned, without picking one', async () => {
    await render(settingsOf());
    expect(q('claude-api-models-loading')).not.toBeNull();
    expect(section().model.disabled).toBe(true);
    await flush();

    expect(q('claude-api-models-loading')).toBeNull();
    expect(
      section()
        .modelOptions()
        .map((o) => o.label),
    ).toEqual(['Test Model B (test-model-b)', 'Test Model A (test-model-a)']);
    expect(section().model.enabled).toBe(true);
    expect(section().model.value).toBeNull();
    expect(q<HTMLButtonElement>('test-claude-api-model')!.disabled).toBe(true);
  });

  it('reloads on Refresh', async () => {
    await render(settingsOf());
    await flush(listOf({ models: [] }));
    await click('refresh-claude-api-models');
    await flush();
    expect(section().modelOptions()).toHaveLength(2);
  });

  it('asks for a key first and loads nothing without one', async () => {
    await render(settingsOf({ claudeApiKeySet: false, claudeApiKeyHint: null }));
    http.expectNone(modelsUrl);
    expect(section().model.disabled).toBe(true);
    expect(q('claude-api-model-no-key')!.textContent).toContain('Save an API key first.');
    expect(q<HTMLButtonElement>('refresh-claude-api-models')!.disabled).toBe(true);
  });

  it('shows the error of an unreachable API and keeps the select disabled', async () => {
    await render(settingsOf());
    await flush(listOf({ reachable: false, error: 'Could not reach the Claude API.', models: [] }));
    expect(q('claude-api-models-error')!.textContent).toContain('Could not reach the Claude API.');
    expect(section().model.disabled).toBe(true);
  });

  it('shows an error that comes with a reachable list', async () => {
    await render(settingsOf());
    await flush(listOf({ error: 'Showing the first 2 models.' }));
    expect(q('claude-api-models-error')!.textContent).toContain('Showing the first 2 models.');
    expect(section().model.enabled).toBe(true);
  });

  it('keeps a saved model the account no longer lists, marked', async () => {
    await render(settingsOf({ claudeApiModel: 'test-model-c' }));
    await flush();
    expect(section().model.value).toBe('test-model-c');
    expect(section().modelOptions().at(-1)).toEqual({
      value: 'test-model-c',
      label: 'test-model-c (not available)',
    });
    expect(q('llm-provider-incomplete')).toBeNull();
  });

  it('tests the chosen model: OK in ms', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('test-claude-api-model');

    const req = http.expectOne(testUrl);
    expect(req.request.body).toEqual({ model: 'test-model-a' });
    expect(q('claude-api-model-testing')).not.toBeNull();
    expect(q<HTMLButtonElement>('test-claude-api-model')!.disabled).toBe(true);
    req.flush({ ok: true, elapsedMs: 420, error: null });
    await fixture.whenStable();
    expect(q('claude-api-model-ok')!.textContent).toContain('Answered in 0.4 s');

    // Another pick drops the old result.
    await choose('test-model-b');
    expect(q('claude-api-model-ok')).toBeNull();
  });

  it('shows the error text of a failed test', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('test-claude-api-model');
    http.expectOne(testUrl).flush({
      ok: false,
      elapsedMs: 0,
      error: 'rejected the API key (HTTP 401). Check the key in Settings.',
    });
    await fixture.whenStable();
    expect(q('claude-api-model-error')!.textContent).toContain('rejected the API key');
  });

  it('asks for a key on a 409 test', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('test-claude-api-model');
    http.expectOne(testUrl).flush(null, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    expect(q('claude-api-model-error')!.textContent).toContain('Save an API key first.');
  });

  it('cancels a running test when the pick leaves Claude API', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('test-claude-api-model');
    const req = http.expectOne(testUrl);
    q('llm-provider-ollama')!.querySelector('input')!.click();
    await fixture.whenStable();
    expect(req.cancelled).toBe(true);
    expect(section().modelTest()).toEqual({ state: 'idle' });
  });

  it('keeps a running test when the list finishes loading', async () => {
    await render(settingsOf({ claudeApiModel: 'test-model-a' }));
    await click('test-claude-api-model');
    const req = http.expectOne(testUrl);
    await flush();
    expect(req.cancelled).toBe(false);
    req.flush({ ok: true, elapsedMs: 1200, error: null });
    await fixture.whenStable();
    expect(q('claude-api-model-ok')!.textContent).toContain('Answered in 1.2 s');
  });

  it('keeps an unsaved pick as an option while the list reloads', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('refresh-claude-api-models');
    const req = http.expectOne(modelsUrl);
    expect(
      section()
        .modelOptions()
        .map((o) => o.value),
    ).toContain('test-model-a');
    req.flush(listOf({ models: [] }));
    await fixture.whenStable();
    expect(section().model.value).toBe('test-model-a');
    expect(section().modelOptions()).toEqual([
      { value: 'test-model-a', label: 'test-model-a (not available)' },
    ]);
  });

  it('reuses the list when switching back to Claude API', async () => {
    await render(settingsOf());
    await flush();
    q('llm-provider-ollama')!.querySelector('input')!.click();
    await fixture.whenStable();
    q('llm-provider-claude_api')!.querySelector('input')!.click();
    await fixture.whenStable();
    http.expectNone(modelsUrl);
    expect(section().modelOptions()).toHaveLength(2);
  });

  it('loads again on a switch back after an unreachable list', async () => {
    await render(settingsOf());
    await flush(listOf({ reachable: false, error: 'Could not reach the Claude API.', models: [] }));
    q('llm-provider-ollama')!.querySelector('input')!.click();
    await fixture.whenStable();
    q('llm-provider-claude_api')!.querySelector('input')!.click();
    await fixture.whenStable();
    await flush();
    expect(section().modelOptions()).toHaveLength(2);
  });

  it('leaves a failed test request to the error interceptor', async () => {
    await render(settingsOf());
    await flush();
    await choose('test-model-a');
    await click('test-claude-api-model');
    http.expectOne(testUrl).flush(null, { status: 502, statusText: 'Bad Gateway' });
    await fixture.whenStable();
    expect(q('claude-api-model-error')).toBeNull();
    expect(section().modelTest()).toEqual({ state: 'idle' });
  });

  it('saves the model with one Save, then the incomplete line goes', async () => {
    await render(settingsOf());
    await flush();
    expect(q('llm-provider-incomplete')).not.toBeNull();
    expect(q<HTMLButtonElement>('save-llm-provider')!.disabled).toBe(true);

    await choose('test-model-a');
    await click('save-llm-provider');
    const req = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/settings');
    expect(req.request.body).toEqual({ claudeApiModel: 'test-model-a' });
    req.flush(settingsOf({ claudeApiModel: 'test-model-a' }));
    await fixture.whenStable();

    expect(emitted.at(-1)!.claudeApiModel).toBe('test-model-a');
    expect(section().model.value).toBe('test-model-a');
    expect(q('llm-provider-incomplete')).toBeNull();
    expect(q<HTMLButtonElement>('save-llm-provider')!.disabled).toBe(true);
  });

  it('saves provider and model together on a switch', async () => {
    await render(settingsOf({ llmProvider: 'ollama' }));
    http.expectNone(modelsUrl);
    q('llm-provider-claude_api')!.querySelector('input')!.click();
    await fixture.whenStable();
    await flush();
    await choose('test-model-b');
    await click('save-llm-provider');
    const req = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/settings');
    expect(req.request.body).toEqual({ llmProvider: 'claude_api', claudeApiModel: 'test-model-b' });
    req.flush(settingsOf({ claudeApiModel: 'test-model-b' }));
    await fixture.whenStable();
  });
});
