import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatTooltip } from '@angular/material/tooltip';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { AnalysisRunDto, canResume, modelCountersText, runBadge } from './analysis.models';
import { AnalysisService } from './analysis.service';
import { FakeJobs, job, run } from './analyse.testing';
import { RunList } from './run-list.component';

describe('run status', () => {
  it('badges a stalled run as stalled and a failed run as failed; only those resume', () => {
    expect(runBadge(run({ status: 'running', isStalled: true }))).toBe('stalled');
    expect(runBadge(run({ status: 'queued', isStalled: true }))).toBe('stalled');
    expect(runBadge(run({ status: 'failed' }))).toBe('failed');
    for (const status of ['running', 'queued', 'completed', 'cancelled'] as const) {
      expect(runBadge(run({ status }))).toBeNull();
      expect(canResume(run({ status }))).toBe(false);
    }
    expect(canResume(run({ status: 'failed' }))).toBe(true);
    expect(canResume(run({ status: 'running', isStalled: true }))).toBe(true);
  });

  it('lists the non-zero triage, escalated, packed and pack retry counters', () => {
    expect(modelCountersText(run())).toBeNull();
    expect(
      modelCountersText(
        run({ triageCalls: 12, escalatedCalls: 3, packedMessages: 1500, packRetries: 1 }),
      ),
    ).toBe(`12 triage calls · 3 escalated · ${(1500).toLocaleString()} packed · 1 pack retry`);
    expect(modelCountersText(run({ triageCalls: 1, packRetries: 2 }))).toBe(
      '1 triage call · 2 pack retries',
    );
  });

  it('resume posts to the run resume endpoint; a status filter reaches the list query', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const service = TestBed.inject(AnalysisService);
    const http = TestBed.inject(HttpTestingController);

    let resumed: unknown;
    service.resume('run-1').subscribe((j) => (resumed = j));
    const req = http.expectOne('/api/analysis/runs/run-1/resume');
    expect(req.request.method).toBe('POST');
    req.flush(job('queued', { id: 'job-2' }), { status: 202, statusText: 'Accepted' });
    expect(resumed).toEqual(job('queued', { id: 'job-2' }));

    service.listRuns(false, 20, 'failed').subscribe();
    http.expectOne('/api/analysis/runs?active=false&limit=20&status=failed').flush([]);
    service.listRuns(false, 20).subscribe();
    http.expectOne('/api/analysis/runs?active=false&limit=20').flush([]);
    http.verify();
  });
});

describe('RunList run status', () => {
  async function render(active: AnalysisRunDto[], finished: AnalysisRunDto[]) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: new FakeJobs() },
        { provide: ClaudeService, useValue: { createReviews: vi.fn() } },
        { provide: SettingsService, useValue: { getSettings: () => of({}) } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(RunList);
    fixture.componentRef.setInput('active', active);
    fixture.componentRef.setInput('finished', finished);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  it('a stalled run shows Stalled and Resume instead of Cancel and progress', async () => {
    const stalled = run({ isStalled: true });
    const { fixture, q } = await render([stalled], []);
    const resumed: AnalysisRunDto[] = [];
    fixture.componentInstance.resumeRun.subscribe((r) => resumed.push(r));

    expect(q('run-stalled')!.textContent).toContain('Stalled');
    expect(q('cancel-run')).toBeNull();
    expect(q('run-progress')).toBeNull();
    q('resume-run')!.click();
    expect(resumed).toEqual([stalled]);

    fixture.componentRef.setInput('resuming', new Set([stalled.id]));
    await fixture.whenStable();
    expect((q('resume-run') as HTMLButtonElement).disabled).toBe(true);
    expect(q('resume-run')!.textContent).toContain('Resuming…');
  });

  it('a failed run carries its error in the tooltip and an expandable line, with Resume', async () => {
    const failed = run({ status: 'failed', error: 'The model did not answer.' });
    const { fixture, q } = await render([], [failed, run({ id: 'run-2', status: 'completed' })]);

    const chip = fixture.debugElement.query(By.css('[data-testid="run-status"]'));
    expect(chip.injector.get(MatTooltip).message).toBe('The model did not answer.');
    expect(q('run-error')!.classList).toContain('truncate');
    q('run-error-toggle')!.click();
    await fixture.whenStable();
    expect(q('run-error')!.classList).not.toContain('truncate');
    expect(q('run-error-toggle')!.getAttribute('aria-expanded')).toBe('true');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="resume-run"]')).toHaveLength(1);
  });

  it('shows the counters only when one is not zero', async () => {
    const { q } = await render(
      [],
      [run({ status: 'completed', triageCalls: 4, escalatedCalls: 1 })],
    );
    expect(q('run-counters')!.textContent).toContain('4 triage calls · 1 escalated');
  });

  it('the Failed chip asks for the failed-only filter; the empty list says so', async () => {
    const { fixture, q } = await render([], []);
    const changes: boolean[] = [];
    fixture.componentInstance.failedOnlyChange.subscribe((f) => changes.push(f));
    q('filter-failed')!.querySelector<HTMLElement>('button')!.click();
    expect(changes).toEqual([true]);
    fixture.componentRef.setInput('failedOnly', true);
    await fixture.whenStable();
    expect(q('no-finished-runs')!.textContent).toContain('No failed runs.');
  });
});
