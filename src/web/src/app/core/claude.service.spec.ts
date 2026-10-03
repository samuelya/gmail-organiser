import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ClaudeService } from './claude.service';

describe('ClaudeService', () => {
  let service: ClaudeService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ClaudeService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('gets the MCP config', () => {
    service.getMcpConfig().subscribe();
    expect(backend.expectOne('/api/claude/mcp-config').request.method).toBe('GET');
  });

  it('rotates the MCP token', () => {
    service.rotateMcpToken().subscribe();
    expect(backend.expectOne('/api/claude/mcp-token/rotate').request.method).toBe('POST');
  });

  it('gets the review prompt as text', () => {
    let prompt = '';
    service.getReviewPrompt().subscribe((p) => (prompt = p));
    const req = backend.expectOne('/api/claude/prompts/review-pending');
    expect(req.request.responseType).toBe('text');
    req.flush('Review the pending items.');
    expect(prompt).toBe('Review the pending items.');
  });

  it('posts a connection test', () => {
    service.testConnection().subscribe();
    expect(backend.expectOne('/api/claude/test').request.method).toBe('POST');
  });

  it('creates review items with the request as the body', () => {
    service.createReviews({ runId: 'run-1' }).subscribe();
    const req = backend.expectOne('/api/claude/reviews');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ runId: 'run-1' });
  });

  it.each(['cancel', 'accept', 'dismiss', 'retry'] as const)('posts %s for an item', (action) => {
    service[action]('item/1').subscribe();
    const req = backend.expectOne(`/api/claude/reviews/item%2F1/${action}`);
    expect(req.request.method).toBe('POST');
  });
});
