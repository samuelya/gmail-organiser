import { OverlayContainer } from '@angular/cdk/overlay';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { JobsIndicator } from './jobs-indicator';

describe('JobsIndicator', () => {
  let activeJobs: ReturnType<typeof signal<JobDto[]>>;
  let connectionState: ReturnType<typeof signal<JobsConnectionState>>;

  const job = (
    id: string,
    type: string,
    done: number,
    total: number | null,
    status: JobDto['status'] = 'running',
  ): JobDto => ({
    id,
    type,
    queue: 'default',
    status,
    progress: { done, total, message: null },
    error: null,
    createdAt: '2026-01-01T00:00:00Z',
    startedAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    finishedAt: null,
    version: 1,
  });

  beforeEach(() => {
    activeJobs = signal<JobDto[]>([]);
    connectionState = signal<JobsConnectionState>('connected');
    TestBed.configureTestingModule({
      imports: [JobsIndicator],
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: { activeJobs, connectionState } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
  });

  // Removes the menu overlay even when a test fails before closing it.
  afterEach(() => TestBed.inject(OverlayContainer).ngOnDestroy());

  async function render() {
    const fixture = TestBed.createComponent(JobsIndicator);
    await fixture.whenStable();
    return fixture;
  }

  const jobsButton = (el: HTMLElement) =>
    el.querySelector<HTMLButtonElement>('[data-testid="jobs-button"]');

  it('shows no button without active jobs', async () => {
    expect(jobsButton((await render()).nativeElement)).toBeNull();
  });

  it('shows the running-jobs count and lists each job in the menu', async () => {
    activeJobs.set([job('j1', 'fetch', 25, 100), job('j2', 'analysis', 3, null, 'queued')]);
    const fixture = await render();
    const button = jobsButton(fixture.nativeElement)!;
    expect(button.getAttribute('aria-label')).toBe('2 jobs running');
    expect(button.querySelector('mat-icon')?.textContent).toMatch(/^\s*sync\d/);
    expect(button.querySelector('.mat-badge-content')?.textContent?.trim()).toBe('2');

    button.click();
    await fixture.whenStable();
    const overlay = TestBed.inject(OverlayContainer).getContainerElement();
    const rows = Array.from(overlay.querySelectorAll('[data-testid="jobs-row"]'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Running');
    expect(rows[0].textContent).toContain('25%');
    expect(rows[1].textContent).toContain('Queued');
    expect(rows[1].textContent).not.toContain('%');
    const open = overlay.querySelector('[data-testid="jobs-open-dashboard"]');
    expect(open?.getAttribute('href')).toBe('/dashboard');
  });

  it('keeps the job count in the label while live updates reconnect', async () => {
    activeJobs.set([job('j1', 'fetch', 1, 10)]);
    connectionState.set('reconnecting');
    const button = jobsButton((await render()).nativeElement)!;
    expect(button.getAttribute('aria-label')).toBe('1 job running, live updates reconnecting');
    expect(button.querySelector('mat-icon')?.textContent).toContain('sync_problem');
  });
});
