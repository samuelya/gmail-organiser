import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { HubConnection } from '@microsoft/signalr';
import { JobDto, JobStatus } from './jobs.models';
import { JOBS_HUB_FACTORY, JobsService, MAX_FINISHED_JOBS, reconnectDelay } from './jobs.service';

/** The parts of `HubConnection` the service uses, driven by the test. */
class StubHub {
  readonly handlers = new Map<string, (arg: unknown) => void>();
  reconnecting?: () => void;
  reconnected?: () => void;
  closed?: () => void;
  start = vi.fn(() => Promise.resolve());
  stop = vi.fn(() => Promise.resolve());

  on(name: string, handler: (arg: unknown) => void) {
    this.handlers.set(name, handler);
  }
  onreconnecting(cb: () => void) {
    this.reconnecting = cb;
  }
  onreconnected(cb: () => void) {
    this.reconnected = cb;
  }
  onclose(cb: () => void) {
    this.closed = cb;
  }
  snapshot(jobs: JobDto[]) {
    this.handlers.get('jobsSnapshot')!(jobs);
  }
  changed(job: JobDto) {
    this.handlers.get('jobChanged')!(job);
  }
}

const job = (id: string, version: number, status: JobStatus = 'running', done = 0): JobDto => ({
  id,
  type: 'test_job',
  queue: 'test',
  status,
  progress: { done, total: 100, message: null },
  error: null,
  createdAt: `2026-01-01T00:00:0${'abc'.indexOf(id)}Z`,
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version,
});

describe('JobsService', () => {
  let hub: StubHub;
  let service: JobsService;

  async function create(start: () => Promise<void> = () => Promise.resolve()) {
    hub = new StubHub();
    hub.start.mockImplementation(start);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: JOBS_HUB_FACTORY, useValue: () => hub as unknown as HubConnection },
      ],
    });
    service = TestBed.inject(JobsService);
    await Promise.resolve();
  }

  afterEach(() => vi.useRealTimers());

  it('connects once when first injected', async () => {
    await create();
    TestBed.inject(JobsService);
    expect(hub.start).toHaveBeenCalledTimes(1);
    expect(service.connectionState()).toBe('connected');
  });

  it('keeps only the latest finished jobs', async () => {
    await create();
    hub.snapshot([job('a', 1)]);
    for (let i = 0; i <= MAX_FINISHED_JOBS; i++) {
      const at = `2026-01-01T00:${String(i).padStart(2, '0')}:00Z`;
      hub.changed({ ...job(`f${i}`, 1, 'completed'), updatedAt: at });
    }
    expect(service.jobs().length).toBe(MAX_FINISHED_JOBS + 1);
    expect(service.job('f0')).toBeUndefined();
    expect(service.job(`f${MAX_FINISHED_JOBS}`)).toBeDefined();
    expect(service.job('a')).toBeDefined();
  });

  it('seeds from the snapshot and applies changes', async () => {
    await create();
    hub.snapshot([job('a', 1), job('b', 1)]);
    hub.changed(job('a', 2, 'running', 40));
    expect(service.job('a')!.progress!.done).toBe(40);
    expect(service.activeJobs().map((j) => j.id)).toEqual(['a', 'b']);
  });

  it('keeps the higher version when a change arrives before an older snapshot', async () => {
    await create();
    hub.changed(job('a', 5, 'paused'));
    hub.snapshot([job('a', 3, 'running')]);
    expect(service.job('a')!.status).toBe('paused');
    expect(service.job('a')!.version).toBe(5);
  });

  it('ignores a change that is not newer', async () => {
    await create();
    hub.snapshot([job('a', 4, 'running', 10)]);
    hub.changed(job('a', 4, 'running', 99));
    hub.changed(job('a', 2, 'paused'));
    expect(service.job('a')!.progress!.done).toBe(10);
  });

  it('finished jobs leave the active list', async () => {
    await create();
    hub.snapshot([job('a', 1)]);
    hub.changed(job('a', 2, 'completed'));
    expect(service.activeJobs()).toEqual([]);
    expect(service.job('a')!.status).toBe('completed');
  });

  it('after a reconnect drops active jobs missing from the new snapshot and bumps reconnects', async () => {
    await create();
    hub.snapshot([job('a', 1), job('b', 1), job('c', 1)]);
    expect(service.reconnects()).toBe(0);

    hub.reconnecting!();
    expect(service.connectionState()).toBe('reconnecting');
    hub.changed(job('c', 2));
    hub.reconnected!();
    hub.snapshot([job('a', 2)]);

    expect(service.connectionState()).toBe('connected');
    expect(service.reconnects()).toBe(1);
    // b finished while offline; c changed on the new connection before its snapshot.
    expect(service.activeJobs().map((j) => j.id)).toEqual(['a', 'c']);
  });

  it('retries a failed first connect with backoff and then counts it as a reconnect', async () => {
    vi.useFakeTimers();
    let fail = true;
    await create(() => (fail ? Promise.reject(new Error('offline')) : Promise.resolve()));
    await vi.advanceTimersByTimeAsync(0);
    expect(service.connectionState()).toBe('disconnected');

    fail = false;
    await vi.advanceTimersByTimeAsync(reconnectDelay(0));
    expect(hub.start).toHaveBeenCalledTimes(2);
    expect(service.connectionState()).toBe('connected');
    expect(service.reconnects()).toBe(1);
  });

  it('restarts after the connection closes', async () => {
    vi.useFakeTimers();
    await create();
    await vi.advanceTimersByTimeAsync(0);
    hub.closed!();
    expect(service.connectionState()).toBe('disconnected');
    await vi.advanceTimersByTimeAsync(reconnectDelay(0));
    expect(hub.start).toHaveBeenCalledTimes(2);
    expect(service.reconnects()).toBe(1);
  });

  it('backs off up to 30 seconds', () => {
    expect([0, 1, 2, 5, 50].map(reconnectDelay)).toEqual([1000, 2000, 4000, 30_000, 30_000]);
  });

  it('a data purge drops every held job and bumps reconnects', async () => {
    await create();
    hub.snapshot([job('a', 1), job('b', 1, 'completed')]);
    const before = service.reconnects();
    hub.handlers.get('dataPurged')!(undefined);
    expect(service.jobs()).toEqual([]);
    expect(service.reconnects()).toBe(before + 1);
    hub.changed(job('a', 1));
    expect(service.activeJobs().map((j) => j.id)).toEqual(['a']);
  });

  it('pause, resume and cancel post to the job endpoints', async () => {
    await create();
    const http = TestBed.inject(HttpTestingController);
    service.pause('a').subscribe();
    service.resume('a').subscribe();
    service.cancel('a').subscribe();
    for (const verb of ['pause', 'resume', 'cancel']) {
      http.expectOne({ method: 'POST', url: `/api/jobs/a/${verb}` }).flush(null);
    }
    http.verify();
  });

  it('fetch reads a job over REST and holds it', async () => {
    await create();
    const http = TestBed.inject(HttpTestingController);
    let fetched: JobDto | undefined;
    service.fetch('a').subscribe((j) => (fetched = j));
    const done = job('a', 2, 'completed');
    http.expectOne({ method: 'GET', url: '/api/jobs/a' }).flush(done);
    expect(fetched).toEqual(done);
    expect(service.job('a')?.status).toBe('completed');
    http.verify();
  });

  it('passes every externalReviewChanged on', async () => {
    await create();
    const seen: unknown[] = [];
    service.externalReviewChanges.subscribe((item) => seen.push(item));
    const item = { id: 'r1', status: 'running' };
    hub.handlers.get('externalReviewChanged')!(item);
    hub.handlers.get('externalReviewChanged')!({ ...item, status: 'reviewed' });
    expect(seen).toEqual([item, { ...item, status: 'reviewed' }]);
  });

  it('stops the connection when destroyed', async () => {
    await create();
    TestBed.resetTestingModule();
    expect(hub.stop).toHaveBeenCalled();
  });
});
