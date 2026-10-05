import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { PagedDto } from '../core/paging.models';
import { SenderDto } from '../senders/senders.models';
import { ProtectionSettingsSection } from './protection-settings.component';
import { ProtectionSettings } from './settings.models';

const rules = (over: Partial<ProtectionSettings> = {}): ProtectionSettings => ({
  attachments: true,
  starred: true,
  important: true,
  repliedThreads: true,
  allowlistedDomains: [],
  ...over,
});

const sender = (address: string, over: Partial<SenderDto> = {}): SenderDto => ({
  address,
  domain: 'example.com',
  displayName: null,
  totalCount: 3,
  analysedCount: 0,
  appliedCount: 0,
  lastSeenAt: null,
  allowlisted: true,
  allowlistedByDomain: false,
  activeFetchJob: null,
  unsubscribedAt: null,
  canonicalAddress: address,
  canonicalDomain: 'example.com',
  isRelay: false,
  kind: 'bulk',
  unreadCount: 0,
  repliedCount: 0,
  firstSeenAt: null,
  ...over,
});

const paged = (items: SenderDto[], total = items.length, page = 1): PagedDto<SenderDto> => ({
  items,
  page,
  pageSize: 50,
  total,
});

describe('ProtectionSettingsSection', () => {
  let fixture: ComponentFixture<ProtectionSettingsSection>;
  let http: HttpTestingController;
  let el: HTMLElement;
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
  const isList = (page: number) => (r: { method: string; urlWithParams: string }) =>
    r.method === 'GET' &&
    r.urlWithParams.startsWith('/api/senders?') &&
    r.urlWithParams.includes(`page=${page}&`) &&
    r.urlWithParams.includes('allowlisted=true');
  const isAllowlistPut = (address: string) => (r: { method: string; url: string }) =>
    r.method === 'PUT' && r.url === `/api/senders/${encodeURIComponent(address)}/allowlist`;

  async function render(list: PagedDto<SenderDto> = paged([sender('b@example.com')])) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProtectionSettingsSection);
    fixture.componentRef.setInput('settings', rules());
    el = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    http.expectOne(isList(1)).flush(list);
    await fixture.whenStable();
  }

  async function typeAddress(value: string) {
    const input = q<HTMLInputElement>('allowlist-address')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    q<HTMLButtonElement>('allowlist-add')!.click();
    await fixture.whenStable();
  }

  afterEach(() => http.verify());

  it('shows a toggle with a hint per rule and states that allowlisted senders are protected', async () => {
    await render();
    for (const key of ['attachments', 'starred', 'important', 'repliedThreads']) {
      expect(q(`rule-${key}`)?.querySelector('button')?.getAttribute('aria-checked')).toBe('true');
      expect(el.querySelector(`#rule-help-${key}`)?.textContent?.trim()).toBeTruthy();
    }
    expect(q('allowlist-always')?.textContent).toContain('always protected');
  });

  it('saves a toggle with only the changed rule', async () => {
    await render();
    q('rule-starred')!.querySelector('button')!.click();
    await fixture.whenStable();
    const req = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/settings');
    expect(req.request.body).toEqual({ protection: { starred: false } });
    req.flush({ protection: rules({ starred: false }) });
    await fixture.whenStable();
    expect(q('rule-starred')!.querySelector('button')!.getAttribute('aria-checked')).toBe('false');
  });

  it('puts a toggle back when its save fails', async () => {
    await render();
    q('rule-important')!.querySelector('button')!.click();
    await fixture.whenStable();
    http
      .expectOne((r) => r.method === 'PUT' && r.url === '/api/settings')
      .flush({ title: 'Bad' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    expect(q('rule-important')!.querySelector('button')!.getAttribute('aria-checked')).toBe('true');
  });

  it('lists allowlisted senders with their counts and loads more', async () => {
    await render(paged([sender('a@example.com', { displayName: 'Example A' })], 2));
    expect(all('allowlist-row')).toHaveLength(1);
    expect(all('allowlist-row')[0].textContent).toContain('Example A');
    expect(all('allowlist-row')[0].textContent).toContain('3 emails');
    q<HTMLButtonElement>('allowlist-more')!.click();
    await fixture.whenStable();
    http.expectOne(isList(2)).flush(paged([sender('c@example.com')], 2, 2));
    await fixture.whenStable();
    expect(all('allowlist-row')).toHaveLength(2);
    expect(q('allowlist-more')).toBeNull();
  });

  it('adds a normalised address', async () => {
    await render();
    await typeAddress('  <New@Example.com> ');
    const req = http.expectOne(isAllowlistPut('new@example.com'));
    expect(req.request.body).toEqual({ allowlisted: true });
    req.flush(sender('new@example.com', { totalCount: 0 }));
    await fixture.whenStable();
    expect(all('allowlist-row').map((r) => r.textContent)).toEqual([
      expect.stringContaining('b@example.com'),
      expect.stringContaining('new@example.com'),
    ]);
    expect(q<HTMLInputElement>('allowlist-address')!.value).toBe('');
  });

  it.each(['example.com', '@example.com', 'a@b@example.com', 'a b@example.com', 'a,b@example.com'])(
    'blocks %s client-side',
    async (value) => {
      await render();
      await typeAddress(value);
      http.expectNone((r) => r.method === 'PUT');
      expect(q('allowlist-error')?.textContent).toContain('Enter one address');
    },
  );

  it('shows the api message for a 400', async () => {
    await render();
    await typeAddress('x@example.com');
    http
      .expectOne(isAllowlistPut('x@example.com'))
      .flush(
        { title: 'One or more validation errors occurred.', errors: { address: ['Not plain.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await fixture.whenStable();
    expect(q('allowlist-error')?.textContent).toContain('Not plain.');
  });

  it('shows the ProblemDetails title for a 400 without a field error', async () => {
    await render();
    await typeAddress('x@example.com');
    http
      .expectOne(isAllowlistPut('x@example.com'))
      .flush({ title: 'Invalid address' }, { status: 400, statusText: 'Bad Request' });
    await fixture.whenStable();
    expect(q('allowlist-error')?.textContent).toContain('Invalid address');
  });

  it('removes a sender from the allowlist', async () => {
    await render();
    q<HTMLButtonElement>('allowlist-remove')!.click();
    await fixture.whenStable();
    const req = http.expectOne(isAllowlistPut('b@example.com'));
    expect(req.request.body).toEqual({ allowlisted: false });
    req.flush(sender('b@example.com', { allowlisted: false }));
    await fixture.whenStable();
    expect(all('allowlist-row')).toHaveLength(0);
    expect(q('allowlist-empty')).not.toBeNull();
  });

  it('reloads the loaded pages after a remove so Load more skips nobody', async () => {
    const first = Array.from({ length: 50 }, (_, i) =>
      sender(`a${String(i).padStart(2, '0')}@example.com`),
    );
    await render(paged(first, 60));
    all('allowlist-remove')[0].click();
    await fixture.whenStable();
    http
      .expectOne(isAllowlistPut('a00@example.com'))
      .flush(sender('a00@example.com', { allowlisted: false }));
    await fixture.whenStable();
    // The server's page 1 now starts at a01 and ends with the sender that was first on page 2.
    http.expectOne(isList(1)).flush(paged([...first.slice(1), sender('b00@example.com')], 59));
    await fixture.whenStable();
    expect(all('allowlist-row')).toHaveLength(50);
    q<HTMLButtonElement>('allowlist-more')!.click();
    await fixture.whenStable();
    const rest = Array.from({ length: 9 }, (_, i) => sender(`b0${i + 1}@example.com`));
    http.expectOne(isList(2)).flush(paged(rest, 59, 2));
    await fixture.whenStable();
    expect(all('allowlist-row')).toHaveLength(59);
    expect(q('allowlist-more')).toBeNull();
  });
});
