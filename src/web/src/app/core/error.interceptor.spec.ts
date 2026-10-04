import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { API_UNREACHABLE, errorInterceptor, QUIET_STATUSES } from './error.interceptor';

describe('errorInterceptor', () => {
  let open: ReturnType<typeof vi.fn>;
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    open = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  function fail(body: string | object | null, status: number, statusText: string) {
    const errors: unknown[] = [];
    http.get('/api/thing').subscribe({ error: (e) => errors.push(e) });
    backend.expectOne('/api/thing').flush(body, { status, statusText });
    expect(errors).toHaveLength(1); // still rethrown to the caller
  }

  it('shows ProblemDetails title and detail', () => {
    fail(
      { title: 'Validation failed', detail: 'Count must be positive.', status: 400 },
      400,
      'Bad Request',
    );
    expect(open).toHaveBeenCalledWith(
      'Validation failed: Count must be positive.',
      'Dismiss',
      expect.anything(),
    );
  });

  it('shows the title alone when there is no detail', () => {
    fail({ title: 'Not found' }, 404, 'Not Found');
    expect(open.mock.calls[0][0]).toBe('Not found');
  });

  it('parses a ProblemDetails string body', () => {
    fail(JSON.stringify({ title: 'Conflict', detail: 'Job already running.' }), 409, 'Conflict');
    expect(open.mock.calls[0][0]).toBe('Conflict: Job already running.');
  });

  it('falls back to the status text for non-ProblemDetails bodies', () => {
    fail(null, 500, 'Internal Server Error');
    expect(open.mock.calls[0][0]).toBe('500 Internal Server Error');
  });

  it('reports network errors as API unreachable', () => {
    const errors: unknown[] = [];
    http.get('/api/thing').subscribe({ error: (e) => errors.push(e) });
    backend.expectOne('/api/thing').error(new ProgressEvent('error'), { status: 0 });
    expect(open.mock.calls[0][0]).toBe(API_UNREACHABLE);
    expect(errors).toHaveLength(1);
  });

  it('stays quiet for the statuses the caller handles', () => {
    const errors: unknown[] = [];
    const context = new HttpContext().set(QUIET_STATUSES, [404]);
    http.get('/api/thing', { context }).subscribe({ error: (e) => errors.push(e) });
    backend.expectOne('/api/thing').flush(null, { status: 404, statusText: 'Not Found' });
    expect(open).not.toHaveBeenCalled();
    expect(errors).toHaveLength(1);
  });
});
