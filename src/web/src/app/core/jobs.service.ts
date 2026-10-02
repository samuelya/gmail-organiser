import { HttpClient } from '@angular/common/http';
import { computed, DestroyRef, inject, Injectable, InjectionToken, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, IRetryPolicy, LogLevel } from '@microsoft/signalr';
import { Observable } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState } from './jobs.models';

export const JOBS_HUB_URL = '/hubs/jobs';

/** 1 s, 2 s, 4 s … capped at 30 s; never gives up, so live updates return when the API does. */
export function reconnectDelay(previousRetryCount: number): number {
  return Math.min(30_000, 1000 * 2 ** Math.min(previousRetryCount, 5));
}

const retryForever: IRetryPolicy = {
  nextRetryDelayInMilliseconds: (context) => reconnectDelay(context.previousRetryCount),
};

/** Builds the hub connection; tests replace it with a stub. */
export const JOBS_HUB_FACTORY = new InjectionToken<() => HubConnection>('JOBS_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => () =>
    new HubConnectionBuilder()
      .withUrl(JOBS_HUB_URL)
      .withAutomaticReconnect(retryForever)
      .configureLogging(LogLevel.Warning)
      .build(),
});

/**
 * Background jobs: live state from `/hubs/jobs` plus pause, resume and cancel. One connection per app,
 * opened when the service is first injected. Every DTO, snapshot item or change, is merged by id
 * keeping the higher `version`, because a `jobChanged` can arrive before the snapshot.
 */
@Injectable({ providedIn: 'root' })
export class JobsService {
  private readonly http = inject(HttpClient);
  private readonly connection = inject(JOBS_HUB_FACTORY)();
  private readonly byId = signal<ReadonlyMap<string, JobDto>>(new Map());
  /** Ids received on the current connection; held active jobs outside it are stale after a reconnect. */
  private readonly seen = new Set<string>();
  private readonly state = signal<JobsConnectionState>('connecting');
  private readonly reconnectCount = signal(0);
  /** Set by every lost or failed connection; the next connect bumps `reconnects`. */
  private missedUpdates = false;
  private startAttempts = 0;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  /** Every job held, active or recently finished. */
  readonly jobs = computed(() => [...this.byId().values()]);
  /** Queued, running and paused jobs, oldest first. */
  readonly activeJobs = computed(() =>
    this.jobs()
      .filter(isActiveJob)
      .sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
  );
  readonly connectionState = this.state.asReadonly();
  /** Increments after every reconnect or late first connect: REST state may have changed meanwhile. */
  readonly reconnects = this.reconnectCount.asReadonly();

  constructor() {
    this.connection.on('jobsSnapshot', (jobs: JobDto[]) => this.onSnapshot(jobs));
    this.connection.on('jobChanged', (job: JobDto) => this.onChanged(job));
    this.connection.onreconnecting(() => {
      this.seen.clear();
      this.missedUpdates = true;
      this.state.set('reconnecting');
    });
    this.connection.onreconnected(() => this.onConnected());
    this.connection.onclose(() => {
      if (this.destroyed) return;
      this.missedUpdates = true;
      this.state.set('disconnected');
      this.scheduleStart();
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      clearTimeout(this.retryTimer);
      void this.connection.stop();
    });
    this.start();
  }

  /** The held state of a job, if any. */
  job(id: string): JobDto | undefined {
    return this.byId().get(id);
  }

  pause(id: string): Observable<void> {
    return this.http.post<void>(`/api/jobs/${encodeURIComponent(id)}/pause`, null);
  }

  resume(id: string): Observable<void> {
    return this.http.post<void>(`/api/jobs/${encodeURIComponent(id)}/resume`, null);
  }

  cancel(id: string): Observable<void> {
    return this.http.post<void>(`/api/jobs/${encodeURIComponent(id)}/cancel`, null);
  }

  private start(): void {
    this.seen.clear();
    this.connection.start().then(
      () => {
        this.startAttempts = 0;
        this.onConnected();
      },
      () => {
        if (this.destroyed) return;
        this.missedUpdates = true;
        this.state.set('disconnected');
        this.scheduleStart();
      },
    );
  }

  private scheduleStart(): void {
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => this.start(), reconnectDelay(this.startAttempts++));
  }

  private onConnected(): void {
    this.state.set('connected');
    if (this.missedUpdates) this.reconnectCount.update((n) => n + 1);
    this.missedUpdates = false;
  }

  private onSnapshot(jobs: JobDto[]): void {
    const snapshotIds = new Set(jobs.map((j) => j.id));
    this.byId.update((current) => {
      const next = new Map(current);
      // Active jobs missing from the snapshot and not changed since finished while disconnected.
      for (const [id, job] of next) {
        if (isActiveJob(job) && !snapshotIds.has(id) && !this.seen.has(id)) next.delete(id);
      }
      for (const job of jobs) merge(next, job);
      return next;
    });
    for (const id of snapshotIds) this.seen.add(id);
  }

  private onChanged(job: JobDto): void {
    this.seen.add(job.id);
    this.byId.update((current) => {
      const held = current.get(job.id);
      if (held && held.version >= job.version) return current;
      return merge(new Map(current), job);
    });
  }
}

function merge(map: Map<string, JobDto>, job: JobDto): Map<string, JobDto> {
  const held = map.get(job.id);
  if (!held || job.version > held.version) map.set(job.id, job);
  return map;
}
