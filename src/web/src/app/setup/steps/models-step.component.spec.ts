import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { LlmModels, LlmService, OllamaModel, TestModelResult } from '../../core/llm.service';
import { SetupState } from '../setup-state';
import { AppSettings, SetupService, UpdateSettingsRequest } from '../setup.service';
import { ModelsStep } from './models-step.component';

const model = (name: string, capabilities: string[]): OllamaModel => ({
  name,
  sizeBytes: 1_300_000_000,
  family: 'test',
  parameterSize: '1B',
  capabilities,
});
const CHAT = model('test-chat:1b', ['completion']);
const EMBED = model('test-embed:1b', ['embedding']);

const reachable = (over: Partial<LlmModels> = {}): LlmModels => ({
  reachable: true,
  version: '0.0.1-test',
  chatModels: [CHAT],
  embeddingModels: [EMBED],
  error: null,
  ...over,
});

const settings = (over: Partial<AppSettings> = {}): AppSettings => ({
  ollamaBaseUrl: 'http://ollama.example.com:11434',
  chatModel: null,
  embeddingModel: null,
  actionLabelName: 'Example-Action',
  deleteLabelName: 'Example-Delete',
  setupWizardSeen: false,
  fetchChunkSize: 500,
  googleClient: { clientId: null, secretSet: false, lockedByEnv: false },
  ...over,
});

describe('ModelsStep', () => {
  let tests: Subject<TestModelResult>;
  let testModel: ReturnType<typeof vi.fn>;
  let saveSettings: ReturnType<typeof vi.fn>;
  let refresh: ReturnType<typeof vi.fn>;

  async function render(list: LlmModels, saved: Partial<AppSettings> = {}) {
    tests = new Subject<TestModelResult>();
    testModel = vi.fn(() => tests);
    saveSettings = vi.fn((r: UpdateSettingsRequest) =>
      of(
        settings({
          chatModel: r.chatModel || null,
          embeddingModel: r.embeddingModel || null,
        }),
      ),
    );
    refresh = vi.fn(() => of(null));
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SetupService,
          useValue: { getSettings: () => of(settings(saved)), saveSettings },
        },
        { provide: LlmService, useValue: { getModels: () => of(list), testModel } },
        { provide: SetupState, useValue: { refresh } },
      ],
    });
    const fixture = TestBed.createComponent(ModelsStep);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, step: fixture.componentInstance, q };
  }

  it('fills each dropdown from its capability list, with size and parameter size', async () => {
    const { step } = await render(reachable());
    expect(step.chatOptions()).toEqual([
      { value: 'test-chat:1b', label: 'test-chat:1b · 1.3 GB · 1B' },
    ]);
    expect(step.embeddingOptions().map((o) => o.value)).toEqual(['test-embed:1b']);
  });

  it('keeps a saved model the server no longer lists', async () => {
    const { step } = await render(reachable(), { chatModel: 'test-gone:1b' });
    expect(step.chatOptions()[0]).toEqual({
      value: 'test-gone:1b',
      label: 'test-gone:1b (not found on the server)',
    });
    expect(step.form.controls.chatModel.value).toBe('test-gone:1b');
  });

  it('an empty list shows the ollama pull hint', async () => {
    const { q } = await render(reachable({ chatModels: [], embeddingModels: [] }));
    expect(q('empty-hint-chat')?.textContent).toContain('ollama pull');
    expect(q('empty-hint-chat')?.textContent).toContain('docs/setup/ollama.md');
    expect(q('empty-hint-embedding')).not.toBeNull();
  });

  it('unreachable Ollama shows the error, not the empty hint', async () => {
    const { q } = await render(
      reachable({ reachable: false, chatModels: [], embeddingModels: [], error: 'Refused' }),
    );
    expect(q('models-unreachable')?.textContent).toContain('Refused');
    expect(q('empty-hint-chat')).toBeNull();
  });

  it('None saves an empty embedding model, which the API stores as null', async () => {
    const { fixture, step, q } = await render(reachable(), {
      chatModel: 'test-chat:1b',
      embeddingModel: 'test-embed:1b',
    });
    step.form.controls.embeddingModel.setValue(null);
    q('save-models')!.click();
    await fixture.whenStable();
    expect(saveSettings).toHaveBeenCalledWith({ chatModel: 'test-chat:1b', embeddingModel: '' });
    expect(step.form.controls.embeddingModel.value).toBeNull();
    expect(refresh).toHaveBeenCalled();
  });

  it('requires a chat model to save', async () => {
    const { q } = await render(reachable());
    q('save-models')!.click();
    expect(saveSettings).not.toHaveBeenCalled();
  });

  it('Test model shows a spinner, then the elapsed seconds, without blocking the form', async () => {
    const { fixture, step, q } = await render(reachable(), { chatModel: 'test-chat:1b' });
    q('test-chat')!.click();
    await fixture.whenStable();
    expect(testModel).toHaveBeenCalledWith('chat', 'test-chat:1b', null);
    expect(q('cancel-test-chat')).not.toBeNull();
    expect(step.form.enabled).toBe(true);
    expect((q('save-models') as HTMLButtonElement).disabled).toBe(false);
    tests.next({ ok: true, elapsedMs: 12_340, error: null });
    await fixture.whenStable();
    expect(q('test-ok-chat')?.textContent).toContain('12.3 s');
  });

  it('Test model shows the error', async () => {
    const { fixture, q } = await render(reachable(), { embeddingModel: 'test-embed:1b' });
    q('test-embedding')!.click();
    tests.next({ ok: false, elapsedMs: 0, error: 'Model not found' });
    await fixture.whenStable();
    expect(q('test-error-embedding')?.textContent).toContain('Model not found');
  });

  it('Cancel unsubscribes and resets the test', async () => {
    const { fixture, step, q } = await render(reachable(), { chatModel: 'test-chat:1b' });
    q('test-chat')!.click();
    await fixture.whenStable();
    q('cancel-test-chat')!.click();
    await fixture.whenStable();
    expect(tests.observed).toBe(false);
    expect(step.chatTest()).toEqual({ state: 'idle' });
  });

  it('Test model is disabled without a selection', async () => {
    const { q } = await render(reachable());
    expect((q('test-chat') as HTMLButtonElement).disabled).toBe(true);
    expect((q('test-embedding') as HTMLButtonElement).disabled).toBe(true);
  });
});
