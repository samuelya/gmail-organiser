import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { errorInterceptor } from '../core/error.interceptor';
import { isActiveJob, JobDto, JobsConnectionState, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { SEARCH_DEBOUNCE_MS, SendersPage } from './senders-page.component';
import { DEFAULT_SENDER_QUERY, SenderDto, SenderQuery } from './senders.models';
import { SendersService } from './senders.service';

const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: 'sender_fetch',
  queue: 'fetch',
  status,
  progress: { done: 40, total: null, message: null },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

const sender = (over: Partial<SenderDto> = {}): SenderDto => ({
  address: 'news@example.com',
  domain: 'example.com',
  displayName: 'Example News',
  totalCount: 10,
  analysedCount: 4,
  appliedCount: 1,
  lastSeenAt: '2026-01-01T00:00:00Z',
  allowlisted: false,
  activeFetchJob: null,
  ...over,
});

const paged = (items: SenderDto[], total = items.length): PagedDto<SenderDto> => ({
  items,
  page: 1,
  pageSize: 50,
  total,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  cancel = vi.fn(() => of(undefined));
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('SendersPage', () => {
  let jobs: FakeJobs;
  let api: { list: ReturnType<typeof vi.fn>; fetchFromSender: ReturnType<typeof vi.fn> };

  async function render(url: string, page: PagedDto<SenderDto>) {
    jobs = new FakeJobs();
    api = {
      list: vi.fn(() => of(page)),
      fetchFromSender: vi.fn(() => of({ jobId: 'job-9' })),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'senders', component: SendersPage }]),
        { provide: JobsService, useValue: jobs },
        { provide: SendersService, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(url, SendersPage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const router = TestBed.inject(Router);
    return { harness, component, el, q, router };
  }

  const lastQuery = (): SenderQuery => api.list.mock.lastCall![0];

  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('loads the query from the URL and shows the rows', async () => {
    const { el } = await render(
      '/senders?search=news&page=2&sort=address&dir=asc',
      paged([sender()], 60),
    );
    expect(lastQuery()).toEqual({
      search: 'news',
      page: 2,
      pageSize: 50,
      sort: 'address',
      dir: 'asc',
    });
    const row = el.querySelector('[data-testid="sender-row"]')!;
    expect(row.textContent).toContain('Example News');
    expect(row.textContent).toContain('news@example.com');
    expect(row.textContent).toMatch(/4\/10/);
  });

  it('paging, page size and sort navigate, and the URL drives list()', async () => {
    const { harness, component, router } = await render('/senders', paged([sender()], 200));
    expect(lastQuery()).toEqual(DEFAULT_SENDER_QUERY);

    component.onPage({ pageIndex: 2, pageSize: 50, length: 200 });
    await harness.fixture.whenStable();
    expect(router.url).toBe('/senders?page=3');
    expect(lastQuery().page).toBe(3);

    component.onPage({ pageIndex: 2, pageSize: 100, length: 200 });
    await harness.fixture.whenStable();
    expect(lastQuery()).toMatchObject({ page: 1, pageSize: 100 });

    component.onSort({ active: 'lastSeen', direction: 'asc' });
    await harness.fixture.whenStable();
    expect(lastQuery()).toMatchObject({ page: 1, pageSize: 100, sort: 'lastSeen', dir: 'asc' });
    expect(router.url).toBe('/senders?pageSize=100&sort=lastSeen&dir=asc');
  });

  it('search is debounced, resets to page 1 and clearing resets it again', async () => {
    const { harness, q, router } = await render('/senders?page=4', paged([sender()], 300));
    const input = q('search') as HTMLInputElement;
    const calls = api.list.mock.calls.length;
    for (const value of ['n', 'ne', 'news ']) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await wait(SEARCH_DEBOUNCE_MS / 2);
    expect(api.list.mock.calls.length).toBe(calls);
    await wait(SEARCH_DEBOUNCE_MS);
    await harness.fixture.whenStable();
    expect(api.list.mock.calls.length).toBe(calls + 1);
    expect(lastQuery()).toMatchObject({ search: 'news', page: 1 });
    expect(router.url).toBe('/senders?search=news');

    q('clear-search')!.click();
    await harness.fixture.whenStable();
    expect(lastQuery()).toMatchObject({ search: '', page: 1 });
    expect(router.url).toBe('/senders');
  });

  it('back/forward puts the search from the URL into the box', async () => {
    const { harness, q } = await render('/senders', paged([sender()]));
    await harness.navigateByUrl('/senders?search=example');
    expect((q('search') as HTMLInputElement).value).toBe('example');
    expect(lastQuery().search).toBe('example');
  });

  it('Fetch all from sender posts the address and reloads the page', async () => {
    const { harness, q } = await render('/senders', paged([sender()]));
    const calls = api.list.mock.calls.length;
    q('fetch-sender')!.click();
    await harness.fixture.whenStable();
    expect(api.fetchFromSender).toHaveBeenCalledWith('news@example.com');
    expect(api.list.mock.calls.length).toBe(calls + 1);
  });

  it('a row with an active job shows its live progress and cancel instead of the button', async () => {
    const active = job('queued');
    const { harness, q } = await render('/senders', paged([sender({ activeFetchJob: active })]));
    expect(q('fetch-sender')).toBeNull();
    expect(q('sender-queued')!.textContent).toContain('Queued');

    jobs.held.set([
      job('running', { version: 2, progress: { done: 7, total: 20, message: null } }),
    ]);
    await harness.fixture.whenStable();
    expect(q('sender-queued')).toBeNull();
    expect(q('sender-done')!.textContent).toContain('7');
    expect(q('sender-cancel')).not.toBeNull();
  });

  it('refetches when a sender fetch finishes, and then shows the button again', async () => {
    const active = job('running');
    const { harness, q } = await render('/senders', paged([sender({ activeFetchJob: active })]));
    const calls = api.list.mock.calls.length;
    api.list.mockReturnValue(of(paged([sender({ totalCount: 30 })])));
    jobs.held.set([job('completed', { version: 5 })]);
    await harness.fixture.whenStable();
    expect(api.list.mock.calls.length).toBe(calls + 1);
    expect(q('fetch-sender')).not.toBeNull();
    expect(q('cell-total')!.textContent).toContain('30');
  });

  it('progress ticks do not refetch', async () => {
    const active = job('running');
    const { harness } = await render('/senders', paged([sender({ activeFetchJob: active })]));
    const calls = api.list.mock.calls.length;
    jobs.held.set([
      job('running', { version: 2, progress: { done: 80, total: null, message: null } }),
    ]);
    await harness.fixture.whenStable();
    expect(api.list.mock.calls.length).toBe(calls);
  });

  it('cancel asks first, then cancels the job', async () => {
    const { harness, q } = await render(
      '/senders',
      paged([sender({ activeFetchJob: job('running') })]),
    );
    q('sender-cancel')!.click();
    await harness.fixture.whenStable();
    const confirm = [
      ...document.querySelectorAll<HTMLButtonElement>('mat-dialog-container button'),
    ].find((b) => b.textContent?.includes('Cancel fetch'));
    confirm!.click();
    await harness.fixture.whenStable();
    expect(jobs.cancel).toHaveBeenCalledWith('job-1');
  });

  it('the toolbar input validates like the API and posts the normalised target', async () => {
    const { harness, q, component } = await render('/senders', paged([sender()]));
    const submit = () => q('target-form')!.dispatchEvent(new Event('submit'));
    component.target.setValue('com');
    submit();
    await harness.fixture.whenStable();
    expect(api.fetchFromSender).not.toHaveBeenCalled();
    expect(q('target-error')).not.toBeNull();

    component.target.setValue('  @Example.ORG ');
    submit();
    await harness.fixture.whenStable();
    expect(api.fetchFromSender).toHaveBeenCalledWith('example.org');
    expect(component.target.value).toBe('');
  });

  it('empty states: no senders yet links to the Dashboard; no search matches', async () => {
    const { harness, q } = await render('/senders', paged([]));
    expect(q('no-senders')!.querySelector('a')!.getAttribute('href')).toBe('/dashboard');
    await harness.navigateByUrl('/senders?search=nothing');
    expect(q('no-senders')).toBeNull();
    expect(q('no-matches')!.textContent).toContain('nothing');
  });
});

describe('SendersPage API errors', () => {
  it('shows the server detail of a 409 in a snackbar', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'senders', component: SendersPage }]),
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: JobsService, useValue: new FakeJobs() },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const open = vi.spyOn(TestBed.inject(MatSnackBar), 'open');
    const http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/senders', SendersPage);
    http.expectOne((r) => r.url === '/api/senders').flush(paged([sender()]));

    component.target.setValue('example.com');
    component.submitTarget();
    http
      .expectOne('/api/fetch/sender')
      .flush(
        {
          title: 'Gmail not connected',
          detail: 'Connect Gmail in Setup before fetching a sender.',
        },
        { status: 409, statusText: 'Conflict' },
      );
    expect(open).toHaveBeenCalledWith(
      expect.stringContaining('Connect Gmail in Setup before fetching a sender.'),
      'Dismiss',
      expect.anything(),
    );
    expect(component.isStarting('example.com')).toBe(false);
    http.verify();
  });
});
