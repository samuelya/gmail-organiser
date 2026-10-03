import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { formatSize, queuedMessage, trashCount } from './clean-up.models';
import { CleanUpService } from './clean-up.service';

describe('CleanUpService', () => {
  let service: CleanUpService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(CleanUpService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the summary', () => {
    service.summary().subscribe();
    http.expectOne('/api/clean-up/summary').flush({ messages: 0, senders: 0, protected: 0 });
  });

  it('lists senders by page and trimmed search', () => {
    service.senders(2, 25, '  news ').subscribe();
    const req = http.expectOne((r) => r.url === '/api/clean-up/senders');
    expect(req.request.params.toString()).toBe('page=2&pageSize=25&search=news');
    req.flush({ items: [], page: 2, pageSize: 25, total: 0 });
  });

  it('leaves a blank search out', () => {
    service.senders(1, 25, '   ').subscribe();
    const req = http.expectOne((r) => r.url === '/api/clean-up/senders');
    expect(req.request.params.has('search')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 25, total: 0 });
  });

  it("encodes the address in a sender's messages", () => {
    service.messages('a+b@example.com', 3, 50).subscribe();
    const req = http.expectOne(
      (r) => r.url === '/api/clean-up/senders/a%2Bb%40example.com/messages',
    );
    expect(req.request.params.toString()).toBe('page=3&pageSize=50');
    req.flush({ items: [], page: 3, pageSize: 50, total: 0 });
  });

  it('sends exactly one selection shape to unmark, without includeProtected', () => {
    service.unmark({ kind: 'ids', ids: ['m1', 'm2'] }).subscribe();
    expect(http.expectOne('/api/clean-up/unmark').request.body).toEqual({
      messageIds: ['m1', 'm2'],
    });
    service.unmark({ kind: 'sender', address: 'news@example.com' }).subscribe();
    expect(http.expectOne('/api/clean-up/unmark').request.body).toEqual({
      senderAddress: 'news@example.com',
    });
    service.unmark({ kind: 'all' }).subscribe();
    expect(http.expectOne('/api/clean-up/unmark').request.body).toEqual({ all: true });
  });

  it('sends the selection and includeProtected to delete', () => {
    service.delete({ kind: 'ids', ids: ['m1'] }, false).subscribe();
    expect(http.expectOne('/api/clean-up/delete').request.body).toEqual({
      messageIds: ['m1'],
      includeProtected: false,
    });
    service.delete({ kind: 'sender', address: 'news@example.com' }, true).subscribe();
    expect(http.expectOne('/api/clean-up/delete').request.body).toEqual({
      senderAddress: 'news@example.com',
      includeProtected: true,
    });
    service.delete({ kind: 'all' }, false).subscribe();
    expect(http.expectOne('/api/clean-up/delete').request.body).toEqual({
      all: true,
      includeProtected: false,
    });
  });

  it('emits null for 204 nothing to do', () => {
    let result: unknown = 'unset';
    service.delete({ kind: 'all' }, false).subscribe((r) => (result = r));
    http.expectOne('/api/clean-up/delete').flush(null, { status: 204, statusText: 'No Content' });
    expect(result).toBeNull();
  });
});

describe('clean-up models', () => {
  const data = { labelName: 'Bin', scope: 'x', count: 10, protectedCount: 3 };

  it('counts what Delete trashes', () => {
    expect(trashCount(data, false)).toBe(7);
    expect(trashCount(data, true)).toBe(10);
  });

  it('describes a queued batch', () => {
    const batch = {
      id: 'b',
      kind: 'trash',
      description: '',
      messageCount: 2,
      jobId: 'j',
      createdAt: '',
    };
    expect(queuedMessage('Moving to Trash', { batch, queued: 2, skippedProtected: 1 })).toBe(
      'Moving to Trash 2 messages; 1 protected message skipped.',
    );
    expect(queuedMessage('Removing', { batch, queued: 1, skippedProtected: 0 })).toBe(
      'Removing 1 message.',
    );
  });

  it('formats sizes', () => {
    expect(formatSize(500)).toBe('500 B');
    expect(formatSize(2048)).toBe('2.0 KB');
    expect(formatSize(5 * 1024 * 1024)).toBe('5.0 MB');
  });
});
