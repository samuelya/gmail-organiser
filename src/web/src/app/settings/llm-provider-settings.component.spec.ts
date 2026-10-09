import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { LlmProviderSettings } from './llm-provider.models';
import { LlmProviderSettingsSection } from './llm-provider-settings.component';
import { SettingsDto } from './settings.models';

const KEY = 'test-key-0000000000000000abcd';

const settingsOf = (over: Partial<LlmProviderSettings> = {}): SettingsDto =>
  ({
    llmProvider: 'ollama',
    claudeApiModel: null,
    claudeApiKeySet: false,
    claudeApiKeyHint: null,
    ...over,
  }) as SettingsDto;

describe('LlmProviderSettingsSection', () => {
  let fixture: ComponentFixture<LlmProviderSettingsSection>;
  let el: HTMLElement;
  let http: HttpTestingController;
  let snackOpen: ReturnType<typeof vi.fn>;
  let emitted: SettingsDto[];
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const overlay = (id: string) =>
    document.querySelector<HTMLElement>(`.cdk-overlay-container [data-testid="${id}"]`);
  const keyUrl = '/api/llm/claude-api/key';

  async function render(settings: SettingsDto | null = settingsOf()) {
    snackOpen = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: snackOpen } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LlmProviderSettingsSection);
    el = fixture.nativeElement as HTMLElement;
    emitted = [];
    // Like the page: each saved settings comes back in as the input.
    fixture.componentInstance.saved.subscribe((s) => {
      emitted.push(s);
      fixture.componentRef.setInput('settings', s);
    });
    fixture.componentRef.setInput('settings', settings);
    await fixture.whenStable();
  }

  async function pick(value: 'ollama' | 'claude_api') {
    q(`llm-provider-${value}`)!.querySelector('input')!.click();
    await fixture.whenStable();
  }

  async function typeKey(value: string) {
    const input = q<HTMLInputElement>('claude-api-key-input')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  afterEach(() => {
    http.verify();
    document.querySelector('.cdk-overlay-container')?.replaceChildren();
  });

  it('stays disabled until the settings load', async () => {
    await render(null);
    expect(q<HTMLButtonElement>('save-llm-provider')!.disabled).toBe(true);
    expect(fixture.componentInstance.provider.disabled).toBe(true);
  });

  it('shows the notices and key field only while Claude API is selected', async () => {
    await render();
    expect(q('llm-provider-privacy')).toBeNull();
    expect(q('llm-provider-ollama-still')).toBeNull();
    expect(q('claude-api-key')).toBeNull();
    expect(q<HTMLButtonElement>('save-llm-provider')!.disabled).toBe(true);

    await pick('claude_api');
    expect(q('llm-provider-privacy')!.textContent).toContain('leaves this machine');
    expect(q('llm-provider-privacy')!.querySelector('mat-icon')!.textContent).toContain(
      'cloud_upload',
    );
    expect(q('llm-provider-ollama-still')!.textContent).toContain('still use Ollama');
    expect(q('claude-api-key-input')).not.toBeNull();
    // Not saved yet: no "will fail" line for an unsaved pick.
    expect(q('llm-provider-incomplete')).toBeNull();

    await pick('ollama');
    expect(q('llm-provider-privacy')).toBeNull();
  });

  it('saves the switch with only llmProvider', async () => {
    await render();
    await pick('claude_api');
    q<HTMLButtonElement>('save-llm-provider')!.click();
    await fixture.whenStable();

    const req = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/settings');
    expect(req.request.body).toEqual({ llmProvider: 'claude_api' });
    req.flush(settingsOf({ llmProvider: 'claude_api' }));
    await fixture.whenStable();

    expect(emitted.at(-1)!.llmProvider).toBe('claude_api');
    expect(snackOpen).toHaveBeenCalledWith('Analysis model provider saved', undefined, {
      duration: 3000,
    });
    expect(q<HTMLButtonElement>('save-llm-provider')!.disabled).toBe(true);
    expect(q('llm-provider-incomplete')!.textContent).toContain('Analysis will fail');
  });

  it('shows a field error from a failed switch', async () => {
    await render();
    await pick('claude_api');
    q<HTMLButtonElement>('save-llm-provider')!.click();
    await fixture.whenStable();
    http
      .expectOne('/api/settings')
      .flush(
        { errors: { llmProvider: ['Must be ollama or claude_api.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('llm-provider-error')!.textContent).toContain('Must be ollama or claude_api.');
    expect(emitted).toEqual([]);

    await pick('ollama');
    expect(q('llm-provider-error')).toBeNull();
  });

  it('drops a typed key and Replace mode when the pick leaves Claude API', async () => {
    await render(
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: '…abcd' }),
    );
    q<HTMLButtonElement>('replace-claude-api-key')!.click();
    await fixture.whenStable();
    await typeKey(KEY);

    await pick('ollama');
    await pick('claude_api');
    expect(fixture.componentInstance.apiKey.value).toBe('');
    expect(fixture.componentInstance.replacing()).toBe(false);
    expect(q('claude-api-key-input')).toBeNull();
    expect(q('claude-api-key-saved')).not.toBeNull();
  });

  it('asks for a new key when the saved one cannot be read', async () => {
    await render(
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: null }),
    );
    const text = q('claude-api-key-saved')!.textContent!;
    expect(text).toContain("can't be read. Replace it.");
    expect(text).not.toContain('ending');
  });

  it('says the key state is out of date when the reload fails', async () => {
    await render(settingsOf({ llmProvider: 'claude_api' }));
    await typeKey(KEY);
    q<HTMLButtonElement>('save-claude-api-key')!.click();
    await fixture.whenStable();
    http.expectOne(keyUrl).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/settings').flush(null, { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();

    expect(q('claude-api-key-reload-error')!.textContent).toContain('Reload the page');
    expect(fixture.componentInstance.apiKey.value).toBe('');
    expect(emitted).toEqual([]);
    expect(q<HTMLButtonElement>('save-claude-api-key')!.disabled).toBe(false);
  });

  it('saves a key, clears the input and shows the hint, never the key', async () => {
    await render(settingsOf({ llmProvider: 'claude_api', claudeApiModel: 'test-model' }));
    const input = q<HTMLInputElement>('claude-api-key-input')!;
    expect(input.type).toBe('password');
    expect(input.getAttribute('autocomplete')).toBe('new-password');
    expect(input.value).toBe('');
    expect(q('llm-provider-incomplete')).not.toBeNull();

    await typeKey(KEY);
    q<HTMLButtonElement>('save-claude-api-key')!.click();
    await fixture.whenStable();

    const put = http.expectOne(keyUrl);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ apiKey: KEY });
    put.flush(null, { status: 204, statusText: 'No Content' });
    expect(fixture.componentInstance.apiKey.value).toBe('');
    http.expectOne('/api/settings').flush(
      settingsOf({
        llmProvider: 'claude_api',
        claudeApiModel: 'test-model',
        claudeApiKeySet: true,
        claudeApiKeyHint: '…abcd',
      }),
    );
    await fixture.whenStable();

    expect(q('claude-api-key-saved')!.textContent).toContain('Key saved, ending …abcd');
    expect(q('claude-api-key-input')).toBeNull();
    expect(q('llm-provider-incomplete')).toBeNull();
    expect(el.innerHTML).not.toContain(KEY);
    expect(snackOpen).toHaveBeenCalledWith('Claude API key saved', undefined, { duration: 3000 });
  });

  it('shows the field error of a rejected key', async () => {
    await render(settingsOf({ llmProvider: 'claude_api' }));
    await typeKey('bad key');
    q<HTMLButtonElement>('save-claude-api-key')!.click();
    await fixture.whenStable();
    http
      .expectOne(keyUrl)
      .flush(
        { errors: { apiKey: ['Must be printable characters without spaces.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('claude-api-key-error')!.textContent).toContain('without spaces');
    expect(emitted).toEqual([]);
  });

  it('does not send an empty key', async () => {
    await render(settingsOf({ llmProvider: 'claude_api' }));
    q<HTMLButtonElement>('save-claude-api-key')!.click();
    await fixture.whenStable();
    http.expectNone(keyUrl);
  });

  it('replaces a saved key through an empty input', async () => {
    await render(
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: '…abcd' }),
    );
    q<HTMLButtonElement>('replace-claude-api-key')!.click();
    await fixture.whenStable();
    expect(q<HTMLInputElement>('claude-api-key-input')!.value).toBe('');

    q<HTMLButtonElement>('cancel-replace-claude-api-key')!.click();
    await fixture.whenStable();
    expect(q('claude-api-key-saved')).not.toBeNull();

    q<HTMLButtonElement>('replace-claude-api-key')!.click();
    await fixture.whenStable();
    await typeKey('test-key-1111111111111111wxyz');
    q<HTMLButtonElement>('save-claude-api-key')!.click();
    await fixture.whenStable();
    http.expectOne(keyUrl).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/settings').flush(
      settingsOf({
        llmProvider: 'claude_api',
        claudeApiKeySet: true,
        claudeApiKeyHint: '…wxyz',
      }),
    );
    await fixture.whenStable();
    expect(q('claude-api-key-saved')!.textContent).toContain('…wxyz');
  });

  it('removes the key only after the confirm', async () => {
    await render(
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: '…abcd' }),
    );
    q<HTMLButtonElement>('remove-claude-api-key')!.click();
    await fixture.whenStable();
    overlay('confirm-cancel')!.click();
    await fixture.whenStable();
    http.expectNone(keyUrl);

    q<HTMLButtonElement>('remove-claude-api-key')!.click();
    await fixture.whenStable();
    overlay('confirm-ok')!.click();
    await fixture.whenStable();
    const del = http.expectOne(keyUrl);
    expect(del.request.method).toBe('DELETE');
    del.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/settings').flush(settingsOf({ llmProvider: 'claude_api' }));
    await fixture.whenStable();

    expect(q('claude-api-key-input')).not.toBeNull();
    expect(q('llm-provider-incomplete')).not.toBeNull();
    expect(snackOpen).toHaveBeenCalledWith('Claude API key removed', undefined, {
      duration: 3000,
    });
  });

  it('keeps an unsaved provider pick when the key state reloads', async () => {
    await render(
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: '…abcd' }),
    );
    fixture.componentRef.setInput(
      'settings',
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: false }),
    );
    await fixture.whenStable();
    expect(fixture.componentInstance.provider.value).toBe('claude_api');
    await pick('ollama');
    fixture.componentRef.setInput(
      'settings',
      settingsOf({ llmProvider: 'claude_api', claudeApiKeySet: true, claudeApiKeyHint: '…abcd' }),
    );
    await fixture.whenStable();
    expect(fixture.componentInstance.provider.value).toBe('ollama');
  });

  it('links to the Claude review card', async () => {
    await render();
    const jumps: string[] = [];
    fixture.componentInstance.jump.subscribe((id) => jumps.push(id));
    const link = q('llm-provider-claude-review')!.querySelector('a')!;
    expect(link.getAttribute('href')).toBe('#settings-claude');
    link.click();
    expect(jumps).toEqual(['settings-claude']);
  });
});
