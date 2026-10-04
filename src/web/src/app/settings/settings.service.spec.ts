import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AppsScriptSettings } from './settings.models';
import { SettingsService } from './settings.service';

const appsScript: AppsScriptSettings = {
  rules: [{ label: 'Receipts', days: 30 }],
  actionDoneArchive: true,
  keepInInboxLabels: [],
  dryRun: true,
};

describe('SettingsService', () => {
  let service: SettingsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SettingsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('saves the whole Apps Script block and caches the response', async () => {
    const saved = firstValueFrom(service.saveAppsScript({ appsScript }));
    const req = http.expectOne('/api/settings');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ appsScript });
    req.flush({ appsScript });
    expect((await saved).appsScript).toEqual(appsScript);

    // The next read is the cached save response, not another GET.
    expect((await firstValueFrom(service.getSettings())).appsScript).toEqual(appsScript);
  });

  it('gets the generated Apps Script config', async () => {
    const config = firstValueFrom(service.appsScriptConfig());
    const req = http.expectOne('/api/rules/apps-script/config');
    expect(req.request.method).toBe('GET');
    req.flush({
      scriptVersion: 1,
      config: 'const CONFIG = {};',
      generatedAt: '2026-01-01T00:00:00Z',
    });
    expect((await config).config).toBe('const CONFIG = {};');
  });
});
