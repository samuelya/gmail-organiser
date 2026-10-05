import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { SetupService } from '../setup/setup.service';
import { PACK_SAVE_DEBOUNCE_MS, PackSettingsSection } from './pack-settings.component';
import { NOT_SAVED } from './triage-model-settings.component';
import { PackSettings } from './triage-settings.models';

const pack = (over: Partial<PackSettings> = {}): PackSettings => ({
  analysisPackSize: 16,
  analysisPackRetryThreshold: 0.6,
  ...over,
});

describe('PackSettingsSection', () => {
  let fixture: ComponentFixture<PackSettingsSection>;
  let component: PackSettingsSection;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';

  async function render(settings: Partial<PackSettings> = pack()) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SetupService);
    fixture = TestBed.createComponent(PackSettingsSection);
    component = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    http.expectOne('/api/settings').flush(settings);
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
  }

  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }));

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('shows the saved values', async () => {
    await render(pack({ analysisPackSize: 1 }));
    expect(q<HTMLInputElement>('pack-size')!.value).toBe('1');
    expect(component.retry.value).toBe(0.6);
    expect(el.textContent).toContain('60 %');
  });

  it('stays disabled on an API without the pack fields', async () => {
    await render({});
    expect(component.size.disabled).toBe(true);
    expect(component.retry.disabled).toBe(true);
  });

  it('refuses a size outside 1–30 or a fraction, and saves a valid one after the debounce', async () => {
    await render();
    for (const bad of ['0', '31', '2.5']) {
      await type('pack-size', bad);
      vi.advanceTimersByTime(PACK_SAVE_DEBOUNCE_MS);
      expect(q('pack-size-error')!.textContent).toContain('whole number from 1 to 30');
    }
    http.expectNone(isPut);

    await type('pack-size', '1');
    vi.advanceTimersByTime(PACK_SAVE_DEBOUNCE_MS);
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ analysisPackSize: 1 });
    put.flush(pack({ analysisPackSize: 1 }));
  });

  it('shows the API message on the size and keeps the value', async () => {
    await render();
    await type('pack-size', '20');
    vi.advanceTimersByTime(PACK_SAVE_DEBOUNCE_MS);
    http
      .expectOne(isPut)
      .flush(
        { errors: { analysisPackSize: ['Must be between 1 and 30.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q<HTMLInputElement>('pack-size')!.value).toBe('20');
    expect(q('pack-size-error')!.textContent).toContain('Must be between 1 and 30.');
  });

  it('saves the retry threshold and marks it not saved when the save fails', async () => {
    await render();
    component.retry.setValue(0.45);
    vi.advanceTimersByTime(PACK_SAVE_DEBOUNCE_MS);
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ analysisPackRetryThreshold: 0.45 });
    put.flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    expect(component.retry.value).toBe(0.45);
    expect(q('pack-retry-error')!.textContent).toContain(NOT_SAVED);
  });
});
