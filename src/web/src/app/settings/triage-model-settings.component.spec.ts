import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { LlmModels } from '../core/llm.service';
import { SetupService } from '../setup/setup.service';
import {
  NOT_SAVED,
  TRIAGE_SAVE_DEBOUNCE_MS,
  TriageModelSettingsSection,
} from './triage-model-settings.component';
import { TriageModelSettings } from './triage-settings.models';

const triage = (over: Partial<TriageModelSettings> = {}): TriageModelSettings => ({
  triageModel: null,
  triageConfidenceThreshold: 0.7,
  ...over,
});

const models: LlmModels = {
  reachable: true,
  version: '0.0.1-test',
  chatModels: [
    { name: 'test-small:1b', sizeBytes: 1, family: 'test', parameterSize: '1B', capabilities: [] },
    { name: 'test-chat:7b', sizeBytes: 1, family: 'test', parameterSize: '7B', capabilities: [] },
  ],
  embeddingModels: [],
  error: null,
};

describe('TriageModelSettingsSection', () => {
  let fixture: ComponentFixture<TriageModelSettingsSection>;
  let component: TriageModelSettingsSection;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';

  async function render(settings: Partial<TriageModelSettings> = triage()) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SetupService);
    fixture = TestBed.createComponent(TriageModelSettingsSection);
    component = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    http.expectOne('/api/settings').flush(settings);
    http.expectOne((r) => r.url === '/api/llm/models').flush(models);
    await fixture.whenStable();
  }

  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }));

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('shows the saved values and lists a saved model the server no longer has', async () => {
    await render(triage({ triageModel: 'gone:1b', triageConfidenceThreshold: 0.8 }));
    expect(component.model.value).toBe('gone:1b');
    expect(component.threshold.value).toBe(0.8);
    expect(component.options().map((o) => o.value)).toEqual([
      'gone:1b',
      'test-small:1b',
      'test-chat:7b',
    ]);
    expect(component.options()[0].label).toContain('not found on the server');
    expect(el.textContent).toContain('80 %');
  });

  it('stays disabled on an API without the triage fields', async () => {
    await render({});
    expect(component.model.disabled).toBe(true);
    expect(component.threshold.disabled).toBe(true);
  });

  it('saves a picked model at once, and None as an empty name', async () => {
    await render();
    component.model.setValue('test-small:1b');
    const pick = http.expectOne(isPut);
    expect(pick.request.body).toEqual({ triageModel: 'test-small:1b' });
    pick.flush(triage({ triageModel: 'test-small:1b' }));

    component.model.setValue(null);
    const none = http.expectOne(isPut);
    expect(none.request.body).toEqual({ triageModel: '' });
    none.flush(triage());
  });

  it('keeps a model whose save failed, marked as not saved', async () => {
    await render();
    component.model.setValue('test-small:1b');
    http.expectOne(isPut).flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    expect(component.model.value).toBe('test-small:1b');
    expect(q('triage-model-error')!.textContent).toContain(NOT_SAVED);
  });

  it('saves the threshold after the debounce and shows the API message on the field', async () => {
    await render();
    component.threshold.setValue(0.75);
    component.threshold.setValue(0.85);
    vi.advanceTimersByTime(TRIAGE_SAVE_DEBOUNCE_MS);
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ triageConfidenceThreshold: 0.85 });
    put.flush(
      { errors: { triageConfidenceThreshold: ['Must be between 0 and 1.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    expect(component.threshold.value).toBe(0.85);
    expect(q('triage-threshold-error')!.textContent).toContain('Must be between 0 and 1.');
  });

  it('tests the picked model as a chat model, and a new pick clears the result', async () => {
    await render();
    expect(q<HTMLButtonElement>('test-triage')!.disabled).toBe(true);
    component.model.setValue('test-small:1b');
    http.expectOne(isPut).flush(triage({ triageModel: 'test-small:1b' }));
    await fixture.whenStable();

    q<HTMLButtonElement>('test-triage')!.click();
    const run = http.expectOne('/api/llm/test-model');
    expect(run.request.body).toEqual({ kind: 'chat', model: 'test-small:1b', baseUrl: null });
    run.flush({ ok: true, elapsedMs: 1500, error: null });
    await fixture.whenStable();
    expect(q('test-ok-triage')!.textContent).toContain('1.5 s');

    component.model.setValue('test-chat:7b');
    http.expectOne(isPut).flush(triage({ triageModel: 'test-chat:7b' }));
    await fixture.whenStable();
    expect(q('test-ok-triage')).toBeNull();
  });
});
