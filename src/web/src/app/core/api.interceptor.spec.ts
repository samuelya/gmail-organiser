import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiInterceptor } from './api.interceptor';

describe('apiInterceptor', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
      ],
    });
  });

  it.each(['GET', 'POST', 'DELETE'])('adds X-Requested-With to %s requests', (method) => {
    TestBed.inject(HttpClient).request(method, '/api/health').subscribe();
    const req = TestBed.inject(HttpTestingController).expectOne('/api/health');
    expect(req.request.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    req.flush({});
  });
});
