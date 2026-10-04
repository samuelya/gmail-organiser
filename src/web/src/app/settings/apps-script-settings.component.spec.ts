import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { LabelDto } from '../review/labels.models';
import { SetupService } from '../setup/setup.service';
import {
  APPS_SCRIPT_GUIDE_PATH,
  APPS_SCRIPT_SAVE_DEBOUNCE_MS,
  AppsScriptSettingsSection,
} from './apps-script-settings.component';
import { AppsScriptConfigDto, AppsScriptSettings } from './settings.models';

const block = (over: Partial<AppsScriptSettings> = {}): AppsScriptSettings => ({
  rules: [{ label: 'Receipts', days: 30 }],
  actionDoneArchive: true,
  keepInInboxLabels: ['Projects/Active'],
  dryRun: true,
  ...over,
});

const config = (text: string): AppsScriptConfigDto => ({
  scriptVersion: 1,
  config: text,
  generatedAt: '2026-01-01T00:00:00Z',
});

const labels: LabelDto[] = [
  { id: 'L1', name: 'Receipts', type: 'user' },
  { id: 'L2', name: 'Newsletters', type: 'user' },
  { id: 'INBOX', name: 'INBOX', type: 'system' },
];

describe('AppsScriptSettingsSection', () => {
  let fixture: ComponentFixture<AppsScriptSettingsSection>;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
  const isConfig = (r: { method: string; url: string }) =>
    r.method === 'GET' && r.url === '/api/rules/apps-script/config';
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';

  async function render(settings = block()) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SetupService);
    fixture = TestBed.createComponent(AppsScriptSettingsSection);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    http.expectOne('/api/settings').flush({ appsScript: settings });
    http.expectOne(isConfig).flush(config('const CONFIG = {};'));
    await fixture.whenStable();
  }

  async function type(id: string, value: string) {
    const input = q<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function click(id: string, index = 0) {
    all(id)[index].click();
    await fixture.whenStable();
  }

  /** Lets the debounce elapse; the save is then pending. */
  async function settle() {
    vi.advanceTimersByTime(APPS_SCRIPT_SAVE_DEBOUNCE_MS);
    await fixture.whenStable();
  }

  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }));

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('shows the loaded rules, toggles, keep labels and config', async () => {
    await render();
    expect(all('rule-row').map((r) => r.textContent)).toEqual([
      expect.stringContaining('Receipts'),
    ]);
    expect(q<HTMLInputElement>('rule-days')!.value).toBe('30');
    expect(q('dry-run')!.querySelector('button')!.getAttribute('aria-checked')).toBe('true');
    expect(all('keep-chip').map((c) => c.textContent?.trim())).toEqual([
      expect.stringContaining('Projects/Active'),
    ]);
    expect(q('config-block')!.textContent).toBe('const CONFIG = {};');
    expect(q<HTMLAnchorElement>('guide-link')!.href).toContain(APPS_SCRIPT_GUIDE_PATH);
    expect(q<HTMLAnchorElement>('guide-link')!.target).toBe('_blank');
  });

  it('adds a rule and saves the whole block, then refreshes the config', async () => {
    await render();
    await type('new-rule-label', 'Newsletters');
    await type('new-rule-days', '14');
    await click('add-rule');
    expect(all('rule-row')).toHaveLength(2);
    http.expectNone(isPut);

    await settle();
    const req = http.expectOne(isPut);
    const saved = block({
      rules: [
        { label: 'Receipts', days: 30 },
        { label: 'Newsletters', days: 14 },
      ],
    });
    expect(req.request.body).toEqual({ appsScript: saved });
    req.flush({ appsScript: saved });
    await fixture.whenStable();

    http.expectOne(isConfig).flush(config('const CONFIG = { rules: 2 };'));
    await fixture.whenStable();
    expect(q('config-block')!.textContent).toBe('const CONFIG = { rules: 2 };');
  });

  it('removes a rule and saves the whole block', async () => {
    await render();
    await click('rule-remove');
    await settle();
    const req = http.expectOne(isPut);
    expect(req.request.body).toEqual({ appsScript: block({ rules: [] }) });
    req.flush({ appsScript: block({ rules: [] }) });
    await fixture.whenStable();
    http.expectOne(isConfig).flush(config(''));
  });

  it('saves a toggle with the rest of the block', async () => {
    await render();
    q('dry-run')!.querySelector('button')!.click();
    await settle();
    const req = http.expectOne(isPut);
    expect(req.request.body).toEqual({ appsScript: block({ dryRun: false }) });
    req.flush({ appsScript: block({ dryRun: false }) });
    await fixture.whenStable();
    http.expectOne(isConfig).flush(config(''));
  });

  it('adds and removes keep-in-inbox labels', async () => {
    await render();
    await type('new-keep-label', 'Newsletters');
    await click('add-keep');
    await click('keep-remove');
    await settle();
    const req = http.expectOne(isPut);
    expect(req.request.body).toEqual({ appsScript: block({ keepInInboxLabels: ['Newsletters'] }) });
    req.flush({});
    await fixture.whenStable();
    http.expectOne(isConfig).flush(config(''));
  });

  it('blocks invalid days client-side', async () => {
    await render();
    await type('new-rule-label', 'Newsletters');
    await type('new-rule-days', '0');
    await click('add-rule');
    expect(all('rule-row')).toHaveLength(1);
    expect(q('new-rule-days-error')).toBeTruthy();

    await type('rule-days', '4000');
    await settle();
    http.expectNone(isPut);
  });

  it('blocks a duplicate rule label client-side, ignoring case and spaces vs dashes', async () => {
    await render(block({ rules: [{ label: 'Paid Bills', days: 30 }] }));
    await type('new-rule-label', 'paid-bills');
    await click('add-rule');
    expect(all('rule-row')).toHaveLength(1);
    expect(q('new-rule-error')!.textContent).toContain('already has a rule');
    await settle();
    http.expectNone(isPut);
  });

  it('blocks a label the script cannot search', async () => {
    await render();
    await type('new-keep-label', 'Bills & Tax');
    await click('add-keep');
    expect(all('keep-chip')).toHaveLength(1);
    expect(q('new-keep-error')).toBeTruthy();
  });

  it('fills the label from the picker', async () => {
    await render();
    await click('new-rule-browse');
    http.expectOne('/api/labels').flush(labels);
    await fixture.whenStable();
    const node = el.querySelector<HTMLElement>('[data-path="Newsletters"]')!;
    node.click();
    await fixture.whenStable();
    expect(q<HTMLInputElement>('new-rule-label')!.value).toBe('Newsletters');
  });

  it('lists the field errors of a 400 under the table', async () => {
    await render();
    await type('rule-days', '45');
    await settle();
    http
      .expectOne(isPut)
      .flush(
        { title: 'Invalid', errors: { 'appsScript.rules[0].label': ['Bad label.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('apps-script-errors')!.textContent).toContain('Rule 1 label: Bad label.');
  });

  it('copies the config text', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await render();
    const open = vi.spyOn(TestBed.inject(MatSnackBar), 'open');
    await click('copy-config');
    await Promise.resolve();
    expect(writeText).toHaveBeenCalledWith('const CONFIG = {};');
    expect(open).toHaveBeenCalledWith('Copied', undefined, expect.anything());
  });
  it('disables Copy from an edit until the saved config has reloaded', async () => {
    await render();
    const copy = () => q<HTMLButtonElement>('copy-config')!;
    q('dry-run')!.querySelector('button')!.click();
    await fixture.whenStable();
    expect(copy().disabled).toBe(true);
    expect(q('config-stale')).not.toBeNull();

    await settle();
    http.expectOne(isPut).flush({ appsScript: block({ dryRun: false }) });
    await fixture.whenStable();
    expect(copy().disabled).toBe(true);

    http.expectOne(isConfig).flush(config('const CONFIG = { dryRun: false };'));
    await fixture.whenStable();
    expect(copy().disabled).toBe(false);
    expect(q('config-stale')).toBeNull();
  });

  it('keeps Copy disabled when the save is refused', async () => {
    await render();
    await type('rule-days', '45');
    await settle();
    http.expectOne(isPut).flush({ title: 'Invalid' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    expect(q<HTMLButtonElement>('copy-config')!.disabled).toBe(true);
  });

  it('keeps Copy disabled for an edit made while the save is in flight', async () => {
    await render();
    await type('rule-days', '45');
    await settle();
    const first = http.expectOne(isPut);
    await type('rule-days', '46');
    first.flush({});
    await fixture.whenStable();
    http.expectOne(isConfig).flush(config('const CONFIG = { days: 45 };'));
    await fixture.whenStable();
    expect(q<HTMLButtonElement>('copy-config')!.disabled).toBe(true);

    await settle();
    http.expectOne(isPut).flush({});
    await fixture.whenStable();
    http.expectOne(isConfig).flush(config('const CONFIG = { days: 46 };'));
    await fixture.whenStable();
    expect(q<HTMLButtonElement>('copy-config')!.disabled).toBe(false);
  });

  it('cancels an older config load when a newer one starts', async () => {
    await render();
    fixture.componentInstance.loadConfig();
    const older = http.expectOne(isConfig);
    fixture.componentInstance.loadConfig();
    expect(older.cancelled).toBe(true);
    http.expectOne(isConfig).flush(config('const CONFIG = { newer: true };'));
    await fixture.whenStable();
    expect(q('config-block')!.textContent).toBe('const CONFIG = { newer: true };');
  });
});
