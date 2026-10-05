import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { isActiveJob, JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SetupService } from '../setup/setup.service';
import {
  RETENTION_SAVE_DEBOUNCE_MS,
  RetentionSettingsSection,
} from './retention-settings.component';
import { AppsScriptSettings } from './settings.models';
import { RetentionSettings, RetentionStatusDto } from './triage-settings.models';

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
  readonly fetch = vi.fn();
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

const retention = (over: Partial<RetentionSettings> = {}): RetentionSettings => ({
  enabled: true,
  days: { personal: null, marketing: 30, security_otp: 7 },
  ...over,
});

const appsScript = (over: Partial<AppsScriptSettings> = {}): AppsScriptSettings => ({
  rules: [{ label: 'Receipts', days: 60 }],
  actionDoneArchive: true,
  keepInInboxLabels: [],
  dryRun: true,
  retentionRules: [],
  ...over,
});

const status = (over: Partial<RetentionStatusDto> = {}): RetentionStatusDto => ({
  enabled: true,
  lastRunAt: null,
  lastMarked: null,
  nextDueAt: '2026-01-02T00:00:00Z',
  eligibleNow: 1234,
  ...over,
});

const job = (over: Partial<JobDto> = {}): JobDto => ({
  id: 'j1',
  type: 'retention_sweep',
  queue: 'gmail',
  status: 'queued',
  progress: null,
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

describe('RetentionSettingsSection', () => {
  let fixture: ComponentFixture<RetentionSettingsSection>;
  let http: HttpTestingController;
  let jobs: FakeJobs;
  let el: HTMLElement;
  const snack = { open: vi.fn() };
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';
  const isStatus = (r: { method: string; url: string }) =>
    r.method === 'GET' && r.url === '/api/clean-up/retention';
  const isRun = (r: { method: string; url: string }) =>
    r.method === 'POST' && r.url === '/api/clean-up/retention/run';

  async function render(settings = { retention: retention(), appsScript: appsScript() }) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: JobsService, useValue: (jobs = new FakeJobs()) },
        { provide: MatSnackBar, useValue: snack },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SetupService);
    fixture = TestBed.createComponent(RetentionSettingsSection);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    http.expectOne('/api/settings').flush(settings);
    http.expectOne(isStatus).flush(status({ enabled: settings.retention.enabled }));
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  async function settle() {
    vi.advanceTimersByTime(RETENTION_SAVE_DEBOUNCE_MS);
    await fixture.whenStable();
  }

  beforeEach(() => {
    snack.open.mockReset();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('shows days per mail type from the API, empty for keep, and the status as an upper bound', async () => {
    await render();
    expect(el.querySelectorAll('[data-testid="retention-row"]').length).toBe(3);
    expect(el.textContent).toContain('Security otp');
    expect(q<HTMLInputElement>('retention-days-personal')!.value).toBe('');
    expect(q<HTMLInputElement>('retention-days-marketing')!.value).toBe('30');
    expect(q('retention-last-run')!.textContent).toContain('Never');
    expect(q('retention-eligible')!.textContent).toContain('up to 1,234');
    expect(q('retention-eligible')!.textContent).toContain('skipped');
  });

  it('saves only the edited type after the debounce, null when cleared', async () => {
    await render();
    await type('retention-days-marketing', '45');
    await type('retention-days-security_otp', '');
    await settle();
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({
      retention: { days: { marketing: 45, security_otp: null } },
    });
    put.flush({ retention: retention() });
    http.expectOne(isStatus).flush(status());
  });

  it('does not save an out-of-range value and shows the API message on its field', async () => {
    await render();
    await type('retention-days-marketing', '0');
    await settle();
    http.expectNone(isPut);
    expect(q('retention-error-marketing')).not.toBeNull();

    await type('retention-days-marketing', '40');
    await settle();
    http
      .expectOne(isPut)
      .flush(
        { title: 'Validation', errors: { 'retention.days.marketing': ['Server says no.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('retention-error-marketing')!.textContent).toContain('Server says no.');
  });

  it('disables Run now while retention is off and enables it once turned on', async () => {
    await render({ retention: retention({ enabled: false }), appsScript: appsScript() });
    expect(q<HTMLButtonElement>('retention-run')!.disabled).toBe(true);
    expect(q('retention-off')).not.toBeNull();

    q<HTMLElement>('retention-enabled')!.querySelector('button')!.click();
    await fixture.whenStable();
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ retention: { enabled: true } });
    put.flush({ retention: retention({ enabled: true }) });
    http.expectOne(isStatus).flush(status());
    await fixture.whenStable();
    expect(q<HTMLButtonElement>('retention-run')!.disabled).toBe(false);
  });

  it('shows a 409 from Run now as a message', async () => {
    await render();
    q<HTMLButtonElement>('retention-run')!.click();
    http
      .expectOne(isRun)
      .flush(
        { title: 'A retention sweep is already active', detail: 'Wait for it to finish.' },
        { status: 409, statusText: 'Conflict' },
      );
    http.expectOne(isStatus).flush(status());
    await fixture.whenStable();
    expect(q('retention-run-message')!.textContent).toContain('already active');
  });

  it('follows the queued sweep and reloads the status when it ends', async () => {
    await render();
    q<HTMLButtonElement>('retention-run')!.click();
    http.expectOne(isRun).flush(job());
    await fixture.whenStable();
    expect(q('retention-progress')).not.toBeNull();
    expect(q<HTMLButtonElement>('retention-run')!.disabled).toBe(true);

    jobs.held.set([
      job({
        status: 'completed',
        version: 2,
        progress: { done: 5, total: 5, message: '5 marked' },
      }),
    ]);
    await fixture.whenStable();
    http.expectOne(isStatus).flush(status({ lastRunAt: '2026-01-01T00:00:00Z', lastMarked: 5 }));
    await fixture.whenStable();
    expect(q('retention-progress')).toBeNull();
    expect(q('retention-last-run')!.textContent).toContain('5 marked');
    expect(snack.open).toHaveBeenCalledWith('5 marked', undefined, { duration: 6000 });
  });

  it('shows a sweep the scheduler started', async () => {
    await render();
    jobs.held.set([job({ status: 'running', progress: { done: 1, total: 4, message: null } })]);
    await fixture.whenStable();
    expect(q('retention-progress')).not.toBeNull();
  });

  it('saves the Apps Script retention rules with the rest of the block and says so', async () => {
    await render();
    const saved = vi.fn();
    fixture.componentInstance.appsScriptSaved.subscribe(saved);
    await type('retention-rule-label', 'Newsletters');
    q<HTMLButtonElement>('retention-rule-add')!.click();
    await settle();
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({
      appsScript: appsScript({ retentionRules: [{ label: 'Newsletters', days: 30 }] }),
    });
    put.flush({ appsScript: appsScript({ retentionRules: [{ label: 'Newsletters', days: 30 }] }) });
    await fixture.whenStable();
    expect(saved).toHaveBeenCalled();
  });

  it('lists a 400 for a retention rule by its row', async () => {
    await render();
    await type('retention-rule-label', 'Old');
    q<HTMLButtonElement>('retention-rule-add')!.click();
    await settle();
    http
      .expectOne(isPut)
      .flush(
        { errors: { 'appsScript.retentionRules[0].label': ['Bad label.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('retention-rule-errors')!.textContent).toContain('Rule 1 label: Bad label.');
  });

  it('refuses a label that already has a retention rule', async () => {
    await render({
      retention: retention(),
      appsScript: appsScript({ retentionRules: [{ label: 'Old', days: 90 }] }),
    });
    await type('retention-rule-label', 'Old');
    q<HTMLButtonElement>('retention-rule-add')!.click();
    await settle();
    http.expectNone(isPut);
    expect(q('retention-rule-label-error')!.textContent).toContain('already has');
  });
});
