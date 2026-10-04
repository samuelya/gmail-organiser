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

  it('lists the newest review items', () => {
    service.list().subscribe();
    const req = backend.expectOne((r) => r.url === '/api/claude/reviews');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('100');
    expect(req.request.params.has('findingId')).toBe(false);
  });

  it('lists the review items of a plan and findings', () => {
    service.list(2, 50, { labelPlanId: 'p1', findingIds: ['f1', 'f2'] }).subscribe();
    const req = backend.expectOne((r) => r.url === '/api/claude/reviews');
    expect(req.request.params.get('labelPlanId')).toBe('p1');
    expect(req.request.params.getAll('findingId')).toEqual(['f1', 'f2']);
    expect(req.request.params.get('page')).toBe('2');
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

  describe('copyReviewPrompt', () => {
    afterEach(() => vi.unstubAllGlobals());

    function stubClipboardWrite(write: () => Promise<void>): void {
      vi.stubGlobal('ClipboardItem', class {});
      vi.stubGlobal('navigator', { clipboard: { write } });
    }

    it('reports copy_failed when the browser refuses the write before the prompt loads', async () => {
      stubClipboardWrite(() => Promise.reject(new DOMException('denied', 'NotAllowedError')));
      const result = service.copyReviewPrompt();
      await Promise.resolve();
      backend.expectOne('/api/claude/prompts/review-pending').flush('Review the pending items.');
      expect(await result).toBe('copy_failed');
    });

    it('reports load_failed when the prompt request fails', async () => {
      stubClipboardWrite(() => Promise.reject(new DOMException('denied', 'NotAllowedError')));
      const result = service.copyReviewPrompt();
      backend
        .expectOne('/api/claude/prompts/review-pending')
        .flush('error', { status: 500, statusText: 'Server Error' });
      expect(await result).toBe('load_failed');
    });

    it('reports copied when the write succeeds', async () => {
      stubClipboardWrite(() => Promise.resolve());
      const result = service.copyReviewPrompt();
      backend.expectOne('/api/claude/prompts/review-pending').flush('Review the pending items.');
      expect(await result).toBe('copied');
    });
  });
});
