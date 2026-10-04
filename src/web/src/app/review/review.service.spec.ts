import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ReviewService } from './review.service';

describe('ReviewService', () => {
  let service: ReviewService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ReviewService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists senders by status, page and trimmed search', () => {
    service.listSenders('approved', '  news ', 2, 25).subscribe();
    const req = http.expectOne((r) => r.url === '/api/review/senders');
    expect(req.request.params.toString()).toBe('status=approved&page=2&pageSize=25&search=news');
    req.flush({ items: [], page: 2, pageSize: 25, total: 0 });
  });

  it('encodes the address in the sender detail and pattern', () => {
    service.sender('a+b@example.com', 'pending', 1, 20).subscribe();
    const detail = http.expectOne((r) => r.url === '/api/review/senders/a%2Bb%40example.com');
    expect(detail.request.params.toString()).toBe('status=pending&page=1&pageSize=20');
    detail.flush({});
    service.pattern('a+b@example.com').subscribe();
    http.expectOne('/api/review/senders/a%2Bb%40example.com/pattern').flush({});
  });

  it("sends the card's outcome with a group approve", () => {
    service
      .approveGroup('news@example.com', 'k', {
        topicLabel: 'T',
        needsAction: true,
        toBeDeleted: false,
        documentTypeLabel: 'Docs/Invoice',
      })
      .subscribe();
    const req = http.expectOne('/api/review/groups/approve');
    expect(req.request.body).toEqual({
      senderAddress: 'news@example.com',
      groupKey: 'k',
      topicLabel: 'T',
      needsAction: true,
      toBeDeleted: false,
      documentTypeLabel: 'Docs/Invoice',
    });
    req.flush({ changed: 1, skipped: [] });
  });

  it('posts apply, apply-rest and analyse individually', () => {
    service.apply('news@example.com').subscribe();
    expect(http.expectOne('/api/review/apply').request.body).toEqual({
      senderAddress: 'news@example.com',
    });
    service.applyRest('news@example.com').subscribe();
    expect(http.expectOne('/api/review/senders/news%40example.com/apply-rest').request.method).toBe(
      'POST',
    );
    service.analyseIndividually(['a', 'b']).subscribe();
    expect(http.expectOne('/api/review/analyse-individually').request.body).toEqual({
      suggestionIds: ['a', 'b'],
    });
  });
});
