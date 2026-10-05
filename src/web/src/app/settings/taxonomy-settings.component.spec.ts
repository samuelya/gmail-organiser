import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { SetupService } from '../setup/setup.service';
import { TAXONOMY_SAVE_DEBOUNCE_MS, TaxonomySettingsSection } from './taxonomy-settings.component';
import { blockedLabelError, TaxonomySettings } from './triage-settings.models';

const taxonomy = (over: Partial<TaxonomySettings> = {}): TaxonomySettings => ({
  taxonomyLocked: false,
  analysisMaxNewLabelsPerRun: 3,
  analysisBlockedLabels: ['Misc'],
  ...over,
});

describe('TaxonomySettingsSection', () => {
  let fixture: ComponentFixture<TaxonomySettingsSection>;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const chips = () =>
    [...el.querySelectorAll('[data-testid="blocked-chip"]')].map((c) => c.textContent!.trim());
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';

  async function render(settings = taxonomy()) {
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
    fixture = TestBed.createComponent(TaxonomySettingsSection);
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
    await render(taxonomy({ taxonomyLocked: true }));
    expect(q('taxonomy-locked')!.querySelector('button')!.getAttribute('aria-checked')).toBe(
      'true',
    );
    expect(q<HTMLInputElement>('taxonomy-max-new')!.value).toBe('3');
    expect(chips()).toEqual([expect.stringContaining('Misc')]);
  });

  it('saves the lock on toggle and puts it back when the save fails', async () => {
    await render();
    q('taxonomy-locked')!.querySelector('button')!.click();
    await fixture.whenStable();
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ taxonomyLocked: true });
    put.flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    expect(fixture.componentInstance.locked.value).toBe(false);
  });

  it('saves a valid max after the debounce and shows the API message on the field', async () => {
    await render();
    await type('taxonomy-max-new', '99');
    vi.advanceTimersByTime(TAXONOMY_SAVE_DEBOUNCE_MS);
    http.expectNone(isPut);
    expect(q('taxonomy-max-new-error')).not.toBeNull();

    await type('taxonomy-max-new', '7');
    vi.advanceTimersByTime(TAXONOMY_SAVE_DEBOUNCE_MS);
    await fixture.whenStable();
    const put = http.expectOne(isPut);
    expect(put.request.body).toEqual({ analysisMaxNewLabelsPerRun: 7 });
    put.flush(
      { errors: { analysisMaxNewLabelsPerRun: ['Server says no.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();
    expect(q('taxonomy-max-new-error')!.textContent).toContain('Server says no.');
  });

  it('adds and removes blocked labels, sending the whole list', async () => {
    await render();
    await type('blocked-new', ' Other ');
    q<HTMLButtonElement>('blocked-add')!.click();
    await fixture.whenStable();
    const add = http.expectOne(isPut);
    expect(add.request.body).toEqual({ analysisBlockedLabels: ['Misc', 'Other'] });
    add.flush(taxonomy({ analysisBlockedLabels: ['Misc', 'Other'] }));
    await fixture.whenStable();

    q<HTMLButtonElement>('blocked-remove')!.click();
    await fixture.whenStable();
    const remove = http.expectOne(isPut);
    expect(remove.request.body).toEqual({ analysisBlockedLabels: ['Other'] });
    remove.flush(taxonomy({ analysisBlockedLabels: ['Other'] }));
  });

  it('refuses a duplicate or a path, and restores the list on a 400', async () => {
    await render();
    await type('blocked-new', 'misc');
    q<HTMLButtonElement>('blocked-add')!.click();
    await fixture.whenStable();
    expect(q('blocked-new-error')!.textContent).toContain('already blocked');

    await type('blocked-new', 'A/B');
    expect(q('blocked-new-error')!.textContent).toContain("without '/'");
    http.expectNone(isPut);

    await type('blocked-new', 'Fine');
    q<HTMLButtonElement>('blocked-add')!.click();
    await fixture.whenStable();
    http
      .expectOne(isPut)
      .flush(
        { errors: { analysisBlockedLabels: ['At most 50 names.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(chips()).toEqual([expect.stringContaining('Misc')]);
    expect(q('blocked-server-error')!.textContent).toContain('At most 50 names.');
  });

  it('keeps the chips of a queued save after an earlier one fails, then shows the server list', async () => {
    await render();
    await type('blocked-new', 'Alpha');
    q<HTMLButtonElement>('blocked-add')!.click();
    await fixture.whenStable();
    await type('blocked-new', 'Beta');
    q<HTMLButtonElement>('blocked-add')!.click();
    await fixture.whenStable();

    http.expectOne(isPut).flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    const named = (...names: string[]) => names.map((n) => expect.stringContaining(n));
    expect(chips()).toEqual(named('Misc', 'Alpha', 'Beta'));

    const queued = http.expectOne(isPut);
    expect(queued.request.body).toEqual({ analysisBlockedLabels: ['Misc', 'Alpha', 'Beta'] });
    queued.flush(taxonomy({ analysisBlockedLabels: ['Misc', 'Alpha', 'Beta'] }));
    await fixture.whenStable();
    expect(chips()).toEqual(named('Misc', 'Alpha', 'Beta'));
  });

  it('blockedLabelError matches the API rules', () => {
    expect(blockedLabelError('Ok')).toBeNull();
    expect(blockedLabelError(' ')).not.toBeNull();
    expect(blockedLabelError('x'.repeat(101))).not.toBeNull();
    expect(blockedLabelError('a\u0001')).not.toBeNull();
  });
});
