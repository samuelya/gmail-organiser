import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogRef } from '@angular/material/dialog';
import { PURGE_CONFLICT_FALLBACK, PurgeDialog } from './purge-dialog.component';
import { PurgeResponse } from './settings.models';

const purged: PurgeResponse = { tables: ['messages', 'senders'], purgedAt: '2026-01-01T00:00:00Z' };

describe('PurgeDialog', () => {
  let fixture: ComponentFixture<PurgeDialog>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let ref: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);

  async function render() {
    ref = { close: vi.fn(), disableClose: false };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatDialogRef, useValue: ref },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PurgeDialog);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  async function type(value: string) {
    const input = q<HTMLInputElement>('purge-confirm')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function run() {
    await type('purge');
    q<HTMLButtonElement>('purge-run')!.click();
    await fixture.whenStable();
  }

  afterEach(() => http.verify());

  it('lists what is removed and kept', async () => {
    await render();
    const copy = q('purge-copy')!.textContent!;
    expect(copy).toContain('message metadata');
    expect(copy).toContain('keeps your Google connection, settings and models');
    expect(copy).toContain('Nothing changes in Gmail');
  });

  it('enables the purge only once the word is typed', async () => {
    await render();
    const button = q<HTMLButtonElement>('purge-run')!;
    expect(button.disabled).toBe(true);
    await type('purg');
    expect(button.disabled).toBe(true);
    await type('Purge');
    expect(button.disabled).toBe(true);
    await type('purge');
    expect(button.disabled).toBe(false);
  });

  it('posts the confirmation and closes with the response', async () => {
    await render();
    await run();
    const req = http.expectOne({ method: 'POST', url: '/api/settings/purge' });
    expect(req.request.body).toEqual({ confirm: 'purge' });
    expect(ref.disableClose).toBe(true);
    req.flush(purged);
    expect(ref.close).toHaveBeenCalledWith(purged);
  });

  it('stays open with the reason on a 409', async () => {
    await render();
    await run();
    http
      .expectOne('/api/settings/purge')
      .flush(
        { title: 'Jobs are active', detail: PURGE_CONFLICT_FALLBACK },
        { status: 409, statusText: 'Conflict' },
      );
    await fixture.whenStable();
    expect(ref.close).not.toHaveBeenCalled();
    expect(ref.disableClose).toBe(false);
    expect(q('purge-error')!.textContent).toContain(PURGE_CONFLICT_FALLBACK);
    expect(q<HTMLButtonElement>('purge-run')!.disabled).toBe(false);
  });

  it('shows the half-applied batch reason, and the fallback for a bare 409', async () => {
    await render();
    await run();
    http
      .expectOne('/api/settings/purge')
      .flush(
        { title: 'A Gmail batch is half-applied', detail: 'Resume it in Jobs.' },
        { status: 409, statusText: 'Conflict' },
      );
    await fixture.whenStable();
    expect(q('purge-error')!.textContent).toContain('A Gmail batch is half-applied');
    q<HTMLButtonElement>('purge-run')!.click();
    await fixture.whenStable();
    http.expectOne('/api/settings/purge').flush(null, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    expect(q('purge-error')!.textContent).toContain(PURGE_CONFLICT_FALLBACK);
  });
});
