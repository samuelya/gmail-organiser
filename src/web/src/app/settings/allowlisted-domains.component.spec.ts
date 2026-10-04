import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { AllowlistedDomains } from './allowlisted-domains.component';
import { coveringDomain, normaliseAllowlistDomain } from './settings.models';

describe('normaliseAllowlistDomain', () => {
  it.each([
    [' Example.COM ', 'example.com'],
    ['mail.example.com.', 'mail.example.com'],
    ['bücher.example', 'xn--bcher-kva.example'],
  ])('normalises %j to %j', (value, expected) => {
    expect(normaliseAllowlistDomain(value)).toBe(expected);
  });

  it.each(['', 'com', 'localhost', 'a@example.com', '1.2.3.4', 'exa mple.com', '-a.example.com'])(
    'rejects %j',
    (value) => expect(normaliseAllowlistDomain(value)).toBeNull(),
  );

  it('finds the listed domain or parent covering a sender domain', () => {
    expect(coveringDomain('mail.example.com', ['example.org', 'example.com'])).toBe('example.com');
    expect(coveringDomain('example.com', ['example.com'])).toBe('example.com');
    expect(coveringDomain('notexample.com', ['example.com'])).toBeNull();
  });
});

describe('AllowlistedDomains', () => {
  let fixture: ComponentFixture<AllowlistedDomains>;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const chips = () =>
    [...el.querySelectorAll('[data-testid="domain-chip"]')].map((c) =>
      c.textContent?.replace('cancel', '').trim(),
    );
  const isPut = (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === '/api/settings';

  async function render(listed: string[] | null = ['example.com']) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AllowlistedDomains);
    fixture.componentRef.setInput('listed', listed);
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  async function type(value: string, key = 'Enter') {
    const input = q<HTMLInputElement>('domain-input')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key, keyCode: key === 'Enter' ? 13 : 188 }));
    await fixture.whenStable();
  }

  afterEach(() => http.verify());

  it('shows a chip per saved domain and stays disabled until loaded', async () => {
    await render(null);
    expect(q<HTMLInputElement>('domain-input')!.disabled).toBe(true);
    fixture.componentRef.setInput('listed', ['example.com', 'example.org']);
    await fixture.whenStable();
    expect(chips()).toEqual(['example.com', 'example.org']);
    expect(q<HTMLInputElement>('domain-input')!.disabled).toBe(false);
  });

  it('adds a normalised domain on Enter and saves the whole list', async () => {
    await render();
    await type(' Mail.Example.ORG. ');
    const req = http.expectOne(isPut);
    expect(req.request.body).toEqual({
      protection: { allowlistedDomains: ['example.com', 'mail.example.org'] },
    });
    req.flush({ protection: { allowlistedDomains: ['example.com', 'mail.example.org'] } });
    await fixture.whenStable();
    expect(chips()).toEqual(['example.com', 'mail.example.org']);
    expect(q<HTMLInputElement>('domain-input')!.value).toBe('');
  });

  it('adds on comma too', async () => {
    await render([]);
    await type('example.net', ',');
    http.expectOne(isPut).flush({ protection: { allowlistedDomains: ['example.net'] } });
    await fixture.whenStable();
    expect(chips()).toEqual(['example.net']);
  });

  it.each(['user@example.com', 'localhost', 'com'])(
    'refuses %j without a request and keeps the text',
    async (value) => {
      await render();
      await type(value);
      http.expectNone(isPut);
      expect(q('domain-error')?.textContent).toContain('is not a domain');
      expect(q<HTMLInputElement>('domain-input')!.value).toBe(value);
    },
  );

  it('removes a chip and saves the rest', async () => {
    await render(['example.com', 'example.org']);
    el.querySelector<HTMLElement>('[data-testid="domain-remove"]')!.click();
    await fixture.whenStable();
    const req = http.expectOne(isPut);
    expect(req.request.body).toEqual({ protection: { allowlistedDomains: ['example.org'] } });
    req.flush({ protection: { allowlistedDomains: ['example.org'] } });
    await fixture.whenStable();
    expect(chips()).toEqual(['example.org']);
  });

  it("shows the api's 400 message for the list", async () => {
    await render();
    await type('example.org');
    http
      .expectOne(isPut)
      .flush(
        { errors: { 'protection.allowlistedDomains': ["'example.org' is not a domain."] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('domain-error')?.textContent).toContain("'example.org' is not a domain.");
    expect(chips()).toEqual(['example.com']);
  });
});
