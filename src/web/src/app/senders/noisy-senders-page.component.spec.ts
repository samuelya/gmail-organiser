import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { convertToParamMap, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, throwError } from 'rxjs';
import { CleanUpService } from '../clean-up/clean-up.service';
import { isActiveJob, JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { FILTER_DEBOUNCE_MS } from './noisy-filter-bar.component';
import { NoisySendersPage } from './noisy-senders-page.component';
import { noisyQueryParams, NoisySenderDto, parseNoisyQuery } from './senders.models';
import { SendersService } from './senders.service';

const noisy = (over: Partial<NoisySenderDto> = {}): NoisySenderDto => ({
  canonicalAddress: 'news@example.com',
  canonicalDomain: 'example.com',
  displayName: 'Example News',
  addresses: ['news@example.com'],
  totalCount: 40,
  unreadCount: 38,
  unreadRatio: 0.95,
  listUnsubscribeCount: 0,
  kind: 'bulk',
  firstSeenAt: '2025-06-01T00:00:00Z',
  lastSeenAt: '2026-01-01T00:00:00Z',
  categoryMix: { primary: 0, promotions: 30, social: 0, updates: 10, forums: 0 },
  unsubscribedAt: null,
  hasApprovedPolicy: false,
  ...over,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  readonly fetch = vi.fn();
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

const refused = new HttpErrorResponse({
  status: 422,
  error: { title: 'Stage-0 action refused', detail: 'person@example.com is a person.' },
});

describe('noisy query mapping', () => {
  it('reads valid params and falls back to the defaults', () => {
    expect(
      parseNoisyQuery(
        convertToParamMap({
          minMessages: '25',
          minUnread: '80',
          dormantDays: '30',
          search: 'shop',
        }),
      ),
    ).toEqual({
      minMessages: 25,
      minUnreadPercent: 80,
      dormantDays: 30,
      search: 'shop',
      page: 1,
      pageSize: 25,
    });
    expect(
      parseNoisyQuery(convertToParamMap({ minMessages: '0', minUnread: '101', dormantDays: 'x' })),
    ).toMatchObject({ minMessages: 10, minUnreadPercent: 90, dormantDays: null });
  });

  it('leaves defaults out of the URL', () => {
    expect(noisyQueryParams(parseNoisyQuery(convertToParamMap({})))).toEqual({
      minMessages: null,
      minUnread: null,
      dormantDays: null,
      search: null,
      page: null,
      pageSize: null,
    });
  });
});

describe('NoisySendersPage', () => {
  let api: {
    listNoisy: ReturnType<typeof vi.fn>;
    proposeNoisy: ReturnType<typeof vi.fn>;
    archiveSenders: ReturnType<typeof vi.fn>;
  };
  let jobs: FakeJobs;

  async function render(rows: NoisySenderDto[], url = '/senders/noisy') {
    api = {
      listNoisy: vi.fn(() => of({ items: rows, page: 1, pageSize: 25, total: rows.length })),
      proposeNoisy: vi.fn(),
      archiveSenders: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'senders/noisy', component: NoisySendersPage }]),
        { provide: JobsService, useValue: (jobs = new FakeJobs()) },
        { provide: SendersService, useValue: api },
        { provide: CleanUpService, useValue: { unsubscribeInfo: () => of(null) } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, NoisySendersPage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string, root: ParentNode = el) =>
      root.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const button = (id: string) => q(id) as HTMLButtonElement;
    const tick = async (row = 0) => {
      const boxes = el.querySelectorAll<HTMLElement>('[data-testid="select-row"] input');
      boxes[row].click();
      await harness.fixture.whenStable();
    };
    return { harness, el, q, button, tick };
  }

  it('lists with the URL filters and writes edited filters back to the URL', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const { harness, q } = await render([noisy()], '/senders/noisy?minUnread=75&dormantDays=60');
      expect(api.listNoisy).toHaveBeenLastCalledWith(
        expect.objectContaining({ minMessages: 10, minUnreadPercent: 75, dormantDays: 60 }),
      );
      const input = q('min-messages') as HTMLInputElement;
      input.value = '20';
      input.dispatchEvent(new Event('input'));
      await vi.advanceTimersByTimeAsync(FILTER_DEBOUNCE_MS);
      await harness.fixture.whenStable();
      const url = TestBed.inject(Router).url;
      expect(url).toContain('minMessages=20');
      expect(url).toContain('minUnread=75');
      expect(api.listNoisy).toHaveBeenLastCalledWith(
        expect.objectContaining({ minMessages: 20, page: 1 }),
      );
    } finally {
      vi.useRealTimers();
    }
  });

  it('enables the toolbar only with a selection and never selects a policy row', async () => {
    const { el, button, tick, q } = await render([
      noisy(),
      noisy({ canonicalAddress: 'deals@example.org', hasApprovedPolicy: true }),
    ]);
    expect(button('archive-all').disabled).toBe(true);
    expect(button('propose').disabled).toBe(true);
    expect(q('policy-chip')).not.toBeNull();
    const policyBox = el.querySelectorAll<HTMLInputElement>('[data-testid="select-row"] input')[1];
    expect(policyBox.disabled).toBe(true);

    await tick(0);
    expect(q('selected-count')!.textContent).toContain('1 selected');
    expect(button('archive-all').disabled).toBe(false);
    expect(button('propose').disabled).toBe(false);
  });

  it('confirms Archive all with the message count, then follows the job', async () => {
    const { harness, button, tick } = await render([
      noisy(),
      noisy({ canonicalAddress: 'shop@example.org', totalCount: 1200 }),
    ]);
    api.archiveSenders.mockReturnValue(
      of({ id: 'job-1', type: 'sender_archive', status: 'queued', progress: null } as JobDto),
    );
    await tick(0);
    await tick(1);
    button('archive-all').click();
    await harness.fixture.whenStable();

    const count = document.querySelector('[data-testid="archive-count"]')!.textContent!;
    expect(count).toContain('1,240 messages');
    expect(count).toContain('2 senders');
    (document.querySelector('[data-testid="archive-ok"]') as HTMLButtonElement).click();
    await harness.fixture.whenStable();

    expect(api.archiveSenders).toHaveBeenCalledWith({
      canonicalAddresses: ['news@example.com', 'shop@example.org'],
    });
    expect(document.querySelector('[data-testid="job-progress"]')).not.toBeNull();
  });

  it('on reconnect, ends an archive that finished while the hub was down', async () => {
    const { harness, button, tick, q } = await render([noisy()]);
    const queued = { id: 'job-1', type: 'sender_archive', status: 'queued', version: 1 } as JobDto;
    api.archiveSenders.mockReturnValue(of(queued));
    await tick(0);
    button('archive-all').click();
    await harness.fixture.whenStable();
    (document.querySelector('[data-testid="archive-ok"]') as HTMLButtonElement).click();
    await harness.fixture.whenStable();
    expect(q('job-progress')).not.toBeNull();

    jobs.fetch.mockReturnValue(of({ ...queued, status: 'completed', version: 3 }));
    const loads = api.listNoisy.mock.calls.length;
    jobs.reconnects.set(1);
    await harness.fixture.whenStable();

    expect(jobs.fetch).toHaveBeenCalledWith('job-1');
    expect(q('job-progress')).toBeNull();
    expect(api.listNoisy.mock.calls.length).toBeGreaterThan(loads);
    await tick(0);
    expect(button('archive-all').disabled).toBe(false);
  });

  it('on reconnect, stops following a picked-up archive that cannot be read', async () => {
    const { harness, button, tick, q } = await render([noisy()]);
    const running = { id: 'job-2', type: 'sender_archive', status: 'running', version: 2 };
    jobs.held.set([running as JobDto]);
    await harness.fixture.whenStable();
    expect(q('job-progress')).not.toBeNull();

    jobs.held.set([]);
    jobs.fetch.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 404 })));
    jobs.reconnects.set(1);
    await harness.fixture.whenStable();

    expect(jobs.fetch).toHaveBeenCalledWith('job-2');
    expect(q('job-progress')).toBeNull();
    await tick(0);
    expect(button('archive-all').disabled).toBe(false);
  });

  it('shows the 422 refusal reason on the page', async () => {
    const { harness, button, tick, q } = await render([noisy()]);
    api.proposeNoisy.mockReturnValue(throwError(() => refused));
    await tick(0);
    (q('also-unsubscribe')!.querySelector('input') as HTMLInputElement).click();
    button('propose').click();
    await harness.fixture.whenStable();

    expect(api.proposeNoisy).toHaveBeenCalledWith({
      canonicalAddresses: ['news@example.com'],
      toBeDeleted: true,
      unsubscribe: true,
    });
    expect(q('action-error')!.textContent).toContain(
      'Stage-0 action refused: person@example.com is a person.',
    );
  });

  it('explains the thresholds when nothing is noisy', async () => {
    const { q } = await render([], '/senders/noisy?minMessages=50');
    expect(q('empty')!.textContent).toContain('at least 50 messages');
    expect(q('empty')!.textContent).toContain('90% or more unread');
  });

  it('shows the ProblemDetails title when the list fails', async () => {
    const { harness, q } = await render([]);
    api.listNoisy.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 400, error: { title: 'Bad filters' } })),
    );
    (harness.routeDebugElement!.componentInstance as NoisySendersPage).reload();
    await harness.fixture.whenStable();
    expect(q('load-error')!.textContent).toContain('Bad filters');
  });
});
