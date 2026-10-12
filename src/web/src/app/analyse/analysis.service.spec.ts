import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AnalysisService } from './analysis.service';

describe('AnalysisService', () => {
  let service: AnalysisService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AnalysisService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists runs without status or kind when not given', () => {
    service.listRuns(true, 50).subscribe();
    const req = http.expectOne((r) => r.url === '/api/analysis/runs');
    expect(req.request.params.toString()).toBe('active=true&limit=50');
    req.flush([]);
  });

  it('lists runs by status and kind', () => {
    service.listRuns(false, 20, 'completed', 'analyse').subscribe();
    const req = http.expectOne((r) => r.url === '/api/analysis/runs');
    expect(req.request.params.toString()).toBe(
      'active=false&limit=20&status=completed&kind=analyse',
    );
    req.flush([]);
  });
});
