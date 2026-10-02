import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { GOOGLE_CONNECT_URL, NAVIGATE_TO, SetupService } from './setup.service';

describe('SetupService', () => {
  let service: SetupService;
  let backend: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    navigate = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE_TO, useValue: navigate },
      ],
    });
    service = TestBed.inject(SetupService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('gets the settings', () => {
    service.getSettings().subscribe();
    expect(backend.expectOne('/api/settings').request.method).toBe('GET');
  });

  it('shares one settings request and serves the last saved settings', () => {
    let seen: unknown[] = [];
    service.getSettings().subscribe((s) => seen.push(s));
    service.getSettings().subscribe((s) => seen.push(s));
    backend.expectOne('/api/settings').flush({ fetchChunkSize: 500 });
    expect(seen).toEqual([{ fetchChunkSize: 500 }, { fetchChunkSize: 500 }]);

    service.saveSettings({ fetchChunkSize: 700 }).subscribe();
    backend.expectOne('/api/settings').flush({ fetchChunkSize: 700 });
    seen = [];
    service.getSettings().subscribe((s) => seen.push(s));
    backend.expectNone('/api/settings');
    expect(seen).toEqual([{ fetchChunkSize: 700 }]);
  });

  it('retries the settings request after a failure', () => {
    service.getSettings().subscribe({ error: () => undefined });
    backend.expectOne('/api/settings').flush(null, { status: 500, statusText: 'Error' });
    service.getSettings().subscribe();
    backend.expectOne('/api/settings');
  });

  it('puts a partial settings update', () => {
    service.saveSettings({ embeddingModel: '' }).subscribe();
    const req = backend.expectOne('/api/settings');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ embeddingModel: '' });
  });

  it('puts the Google client', () => {
    const body = { clientId: 'abc.apps.googleusercontent.com', clientSecret: 'secret' };
    service.saveGoogleClient(body).subscribe();
    const req = backend.expectOne('/api/settings/google-client');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(body);
  });

  it('gets the setup status', () => {
    service.getSetupStatus().subscribe();
    expect(backend.expectOne('/api/setup/status').request.method).toBe('GET');
  });

  it('gets the Google auth status', () => {
    service.getGoogleStatus().subscribe();
    expect(backend.expectOne('/api/auth/google/status').request.method).toBe('GET');
  });

  it('posts disconnect', () => {
    service.disconnectGoogle().subscribe();
    expect(backend.expectOne('/api/auth/google/disconnect').request.method).toBe('POST');
  });

  it('connects by navigating the window, not by XHR', () => {
    service.connectGoogle();
    expect(navigate).toHaveBeenCalledWith(GOOGLE_CONNECT_URL);
    backend.expectNone(GOOGLE_CONNECT_URL);
  });
});
