import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FetchService } from './fetch.service';

describe('FetchService', () => {
  let service: FetchService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(FetchService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('getStatus reads /api/fetch/status', () => {
    let phase = '';
    service.getStatus().subscribe((s) => (phase = s.mailboxPhase));
    http.expectOne({ method: 'GET', url: '/api/fetch/status' }).flush({ mailboxPhase: 'inbox' });
    expect(phase).toBe('inbox');
  });

  it('startMailboxFetch posts and returns the job id for 202 and 200 alike', () => {
    const ids: string[] = [];
    service.startMailboxFetch().subscribe((r) => ids.push(r.jobId));
    service.startMailboxFetch().subscribe((r) => ids.push(r.jobId));
    const [created, resumed] = http.match({ method: 'POST', url: '/api/fetch/mailbox/start' });
    created.flush({ jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    resumed.flush({ jobId: 'job-2' });
    expect(ids).toEqual(['job-1', 'job-2']);
  });

  it('resyncLabels posts to /api/fetch/labels/resync and returns the job id', () => {
    let id = '';
    service.resyncLabels().subscribe((r) => (id = r.jobId));
    const req = http.expectOne({ method: 'POST', url: '/api/fetch/labels/resync' });
    expect(req.request.body).toBeNull();
    req.flush({ jobId: 'job-3' }, { status: 202, statusText: 'Accepted' });
    expect(id).toBe('job-3');
  });
});
