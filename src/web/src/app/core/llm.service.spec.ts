import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { formatBytes, LlmService, modelLabel } from './llm.service';

describe('LlmService', () => {
  let service: LlmService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(LlmService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('lists models for the saved URL without a baseUrl parameter', () => {
    service.getModels().subscribe();
    const req = backend.expectOne((r) => r.url === '/api/llm/models');
    expect(req.request.params.has('baseUrl')).toBe(false);
  });

  it('lists models for an unsaved URL', () => {
    service.getModels('http://ollama.example.com:11434').subscribe();
    const req = backend.expectOne((r) => r.url === '/api/llm/models');
    expect(req.request.params.get('baseUrl')).toBe('http://ollama.example.com:11434');
  });

  it('posts a model test', () => {
    service.testModel('chat', 'test-chat:1b').subscribe();
    const req = backend.expectOne('/api/llm/test-model');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ kind: 'chat', model: 'test-chat:1b', baseUrl: null });
  });

  it('aborts the model test on unsubscribe', () => {
    const sub = service.testModel('embedding', 'test-embed:1b').subscribe();
    const req = backend.expectOne('/api/llm/test-model');
    sub.unsubscribe();
    expect(req.cancelled).toBe(true);
  });

  it('formats sizes and labels', () => {
    expect(formatBytes(4_700_000_000)).toBe('4.7 GB');
    expect(formatBytes(274_000_000)).toBe('274 MB');
    expect(formatBytes(0)).toBe('');
    expect(
      modelLabel({
        name: 'test-chat:1b',
        sizeBytes: 1_300_000_000,
        family: null,
        parameterSize: '1B',
        capabilities: [],
      }),
    ).toBe('test-chat:1b · 1.3 GB · 1B');
  });
});
