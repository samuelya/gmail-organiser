import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';
import { errorInterceptor } from './error.interceptor';
import { VersionService } from './version.service';

describe('VersionService', () => {
  let http: HttpTestingController;
  let snackOpen: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    snackOpen = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: snackOpen } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the version from /healthz', async () => {
    const version = firstValueFrom(TestBed.inject(VersionService).getVersion());
    http.expectOne('/healthz').flush({ status: 'ok', version: '1.2.3' });
    expect(await version).toBe('1.2.3');
  });

  it('reads the version from a 503 without a snackbar', async () => {
    const version = firstValueFrom(TestBed.inject(VersionService).getVersion());
    http
      .expectOne('/healthz')
      .flush({ status: 'unhealthy', version: '1.2.3' }, { status: 503, statusText: 'Unavailable' });
    expect(await version).toBe('1.2.3');
    expect(snackOpen).not.toHaveBeenCalled();
  });
});
