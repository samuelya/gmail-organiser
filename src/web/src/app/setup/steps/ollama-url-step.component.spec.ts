import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { LlmModels, LlmService } from '../../core/llm.service';
import { AppSettings, SetupService } from '../setup.service';
import { httpUrlValidator, OllamaUrlStep } from './ollama-url-step.component';
import { FormControl } from '@angular/forms';

const URL = 'http://ollama.example.com:11434';

const settings = (ollamaBaseUrl = URL): AppSettings => ({
  ollamaBaseUrl,
  chatModel: null,
  embeddingModel: null,
  actionLabelName: 'Example-Action',
  deleteLabelName: 'Example-Delete',
  setupWizardSeen: false,
  googleClient: { clientId: null, secretSet: false, lockedByEnv: false },
});

describe('OllamaUrlStep', () => {
  let models: Subject<LlmModels>;
  let getModels: ReturnType<typeof vi.fn>;
  let saveSettings: ReturnType<typeof vi.fn>;

  async function render() {
    models = new Subject<LlmModels>();
    getModels = vi.fn(() => models);
    saveSettings = vi.fn((r: { ollamaBaseUrl: string }) => of(settings(r.ollamaBaseUrl)));
    TestBed.configureTestingModule({
      providers: [
        { provide: SetupService, useValue: { getSettings: () => of(settings()), saveSettings } },
        { provide: LlmService, useValue: { getModels } },
      ],
    });
    const fixture = TestBed.createComponent(OllamaUrlStep);
    const saved: string[] = [];
    fixture.componentInstance.saved.subscribe((url) => saved.push(url));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, step: fixture.componentInstance, el, q, saved };
  }

  it('prefills the URL from the settings', async () => {
    const { q } = await render();
    expect((q('ollama-url') as HTMLInputElement).value).toBe(URL);
  });

  it('Test URL checks the unsaved value and shows the version', async () => {
    const { fixture, step, q } = await render();
    step.url.setValue('http://other.example.com:11434');
    q('test-url')!.click();
    expect(getModels).toHaveBeenCalledWith('http://other.example.com:11434');
    models.next({
      reachable: true,
      version: '0.0.1-test',
      chatModels: [],
      embeddingModels: [],
      error: null,
    });
    await fixture.whenStable();
    expect(q('url-ok')?.textContent).toContain('0.0.1-test');
    expect(q('url-hint')).toBeNull();
  });

  it('an unreachable URL shows the error and the outside-Docker hint', async () => {
    const { fixture, q } = await render();
    q('test-url')!.click();
    models.next({
      reachable: false,
      version: null,
      chatModels: [],
      embeddingModels: [],
      error: 'Connection refused',
    });
    await fixture.whenStable();
    expect(q('url-error')?.textContent).toContain('Connection refused');
    expect(q('url-hint')?.textContent).toContain('outside Docker');
  });

  it('saves the URL and emits it', async () => {
    const { fixture, step, q, saved } = await render();
    step.url.setValue(' http://saved.example.com:11434 ');
    q('save-url')!.click();
    await fixture.whenStable();
    expect(saveSettings).toHaveBeenCalledWith({ ollamaBaseUrl: 'http://saved.example.com:11434' });
    expect(saved).toEqual(['http://saved.example.com:11434']);
  });

  it('does not save or test an invalid URL', async () => {
    const { step, q } = await render();
    step.url.setValue('ftp://example.com');
    q('save-url')!.click();
    q('test-url')!.click();
    expect(saveSettings).not.toHaveBeenCalled();
    expect(getModels).not.toHaveBeenCalled();
  });

  it('accepts only http and https URLs', () => {
    const check = (v: string) => httpUrlValidator(new FormControl(v, { nonNullable: true }));
    expect(check(URL)).toBeNull();
    expect(check('https://ollama.example.com')).toBeNull();
    expect(check('')).toBeNull();
    expect(check('not a url')).toEqual({ httpUrl: true });
    expect(check('ftp://example.com')).toEqual({ httpUrl: true });
  });
});
