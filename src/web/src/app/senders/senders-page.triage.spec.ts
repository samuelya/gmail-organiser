import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { SendersPage } from './senders-page.component';
import { SenderDto } from './senders.models';
import { SendersService } from './senders.service';

const sender = (over: Partial<SenderDto> = {}): SenderDto => ({
  address: 'news@example.com',
  domain: 'example.com',
  displayName: 'Example News',
  totalCount: 10,
  analysedCount: 4,
  appliedCount: 1,
  lastSeenAt: '2026-01-01T00:00:00Z',
  allowlisted: false,
  allowlistedByDomain: false,
  activeFetchJob: null,
  unsubscribedAt: null,
  canonicalAddress: 'news@example.com',
  canonicalDomain: 'example.com',
  isRelay: false,
  kind: 'bulk',
  unreadCount: 9,
  repliedCount: 0,
  firstSeenAt: '2025-06-01T00:00:00Z',
  ...over,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

describe('SendersPage Stage-0 columns and kind filter', () => {
  let api: { list: ReturnType<typeof vi.fn>; fetchFromSender: ReturnType<typeof vi.fn> };

  async function render(rows: SenderDto[], url = '/senders') {
    api = {
      list: vi.fn(() => of({ items: rows, page: 1, pageSize: 50, total: rows.length })),
      fetchFromSender: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'senders', component: SendersPage }]),
        { provide: JobsService, useValue: new FakeJobs() },
        { provide: SendersService, useValue: api },
        { provide: SettingsService, useValue: { getSettings: () => of(null) } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, SendersPage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string, root: ParentNode = el) =>
      root.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const rowsOf = () => [...el.querySelectorAll<HTMLElement>('[data-testid="sender-row"]')];
    return { harness, el, q, rowsOf };
  }

  it('shows the relay badge and the raw address only for a relayed sender', async () => {
    const relayed = sender({
      address: 'bounce-1@relay.example.net',
      domain: 'relay.example.net',
      canonicalAddress: 'shop@example.org',
      canonicalDomain: 'example.org',
      isRelay: true,
      displayName: null,
    });
    const { q, rowsOf } = await render([relayed, sender()]);
    const [first, second] = rowsOf();

    expect(q('canonical-address', first)!.textContent!.trim()).toBe('shop@example.org');
    expect(q('canonical-address', first)!.getAttribute('aria-label')).toContain(
      'sent as bounce-1@relay.example.net',
    );
    expect(q('relay-chip', first)?.textContent).toContain('Relay');
    expect(first.textContent).toContain('example.org');

    expect(q('relay-chip', second)).toBeNull();
    expect(q('canonical-address', second)!.getAttribute('aria-label')).toBeNull();
  });

  it('renders the kind chip, unread % with count, replied icon and first seen', async () => {
    const { q, rowsOf } = await render([
      sender({ kind: 'human', unreadCount: 1, totalCount: 3, repliedCount: 2 }),
      sender({ address: 'b@example.com', kind: 'mixed', repliedCount: 0 }),
    ]);
    const [human, mixed] = rowsOf();

    expect(q('kind-chip', human)!.textContent!.trim()).toBe('Human');
    expect(q('kind-chip', human)!.classList).toContain('kind-human');
    expect(q('kind-chip', mixed)!.classList).toContain('kind-mixed');
    expect(q('cell-unread', human)!.textContent!.replace(/\s+/g, ' ').trim()).toBe('33% (1)');
    expect(q('replied-icon', human)!.getAttribute('aria-label')).toBe('Replied 2 times');
    expect(q('replied-icon', mixed)).toBeNull();
    expect(human.textContent).toContain('2025');
  });

  it('shows an Unsubscribed chip only on a row with unsubscribedAt', async () => {
    const { el } = await render([
      sender(),
      sender({
        address: 'shop@example.com',
        canonicalAddress: 'shop@example.com',
        unsubscribedAt: '2026-03-04T10:00:00Z',
      }),
    ]);
    const chips = el.querySelectorAll('[data-testid="unsubscribed-chip"]');
    expect(chips).toHaveLength(1);
    expect(chips[0].textContent).toContain('Unsubscribed Mar 4, 2026');
    expect(chips[0].closest('[data-testid="sender-row"]')!.textContent).toContain(
      'shop@example.com',
    );
  });

  it('empty states: no senders yet links to the Dashboard; no search matches', async () => {
    const { harness, q } = await render([]);
    expect(q('no-senders')!.querySelector('a')!.getAttribute('href')).toBe('/dashboard');
    await harness.navigateByUrl('/senders?search=nothing');
    expect(q('no-senders')).toBeNull();
    expect(q('no-matches')!.textContent).toContain('nothing');
  });

  it('reads the kinds and unread sort from the URL and sends them', async () => {
    await render([sender()], '/senders?kind=mixed&kind=bulk&sort=unread&dir=asc');
    expect(api.list).toHaveBeenLastCalledWith(
      expect.objectContaining({ kinds: ['bulk', 'mixed'], sort: 'unread', dir: 'asc' }),
    );
  });

  it('selecting a kind chip puts it in the URL and goes back to page 1', async () => {
    const { harness, q } = await render([sender()], '/senders?page=2&kind=human');
    const options = q('kind-filter')!.querySelectorAll<HTMLElement>('mat-chip-option');
    const bulk = [...options].find((o) => o.textContent!.includes('Bulk'))!;

    bulk.querySelector<HTMLElement>('button')!.click();
    await harness.fixture.whenStable();

    const url = TestBed.inject(Router).url;
    expect(url).toBe('/senders?kind=human&kind=bulk');
    expect(api.list).toHaveBeenLastCalledWith(
      expect.objectContaining({ kinds: ['human', 'bulk'], page: 1 }),
    );
  });

  it('says no senders match when the kind filter leaves none', async () => {
    const { q } = await render([], '/senders?kind=human');
    expect(q('no-matches')?.textContent).toContain('selected kinds');
  });
});
