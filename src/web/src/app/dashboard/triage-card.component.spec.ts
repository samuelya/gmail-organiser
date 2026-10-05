import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of, Subject, throwError } from 'rxjs';
import { TriageCard } from './triage-card.component';
import { MetricPointDto, MetricSnapshotDto, TriageMetricsDto } from './triage.models';
import { TriageService } from './triage.service';

const snapshot = (over: Partial<MetricSnapshotDto> = {}): MetricSnapshotDto => ({
  takenAt: '2026-01-03T10:00:00Z',
  messagesTotal: 1000,
  inboxCount: 80,
  inboxUnreadCount: 1234,
  coveredByPolicy: 425,
  coveredByFilter: 100,
  analysed: 600,
  applied: 300,
  toBeDeleted: 20,
  llmMillisecondsTotal: 9_000_000,
  promptTokensTotal: 5000,
  policyCoverageRatio: 0.425,
  filterCoverageRatio: 0.1,
  analysedRatio: 0.6,
  inboxUnreadRatio: 0.5,
  ...over,
});

const day = (d: string, unread: number, covered: number): MetricPointDto => ({
  day: d,
  takenAt: `${d}T23:00:00Z`,
  messagesTotal: 1000,
  inboxCount: 80,
  inboxUnreadCount: unread,
  coveredByPolicy: covered,
  coveredByFilter: 0,
  analysed: 0,
  applied: 0,
  toBeDeleted: 0,
  llmHours: 0,
});

const metrics = (over: Partial<TriageMetricsDto> = {}): TriageMetricsDto => ({
  current: snapshot(),
  history: [day('2026-01-01', 40, 100), day('2026-01-02', 30, 300), day('2026-01-03', 20, 425)],
  llmHours: 2.5,
  ...over,
});

describe('TriageCard', () => {
  let triage: { get: ReturnType<typeof vi.fn>; snapshot: ReturnType<typeof vi.fn> };

  async function render(initial: TriageMetricsDto) {
    triage = { get: vi.fn(() => of(initial)), snapshot: vi.fn(() => of(initial)) };
    TestBed.configureTestingModule({
      providers: [
        { provide: TriageService, useValue: triage },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(TriageCard);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  it('shows the four figures and the snapshot time', async () => {
    const { q } = await render(metrics());
    expect(q('triage-policy')?.textContent?.trim()).toBe('42.5%');
    expect(q('triage-filter')?.textContent?.trim()).toBe('10.0%');
    expect(q('triage-unread')?.textContent).toContain('1,234');
    expect(q('triage-unread')?.textContent).toContain('of 80');
    expect(q('triage-llm')?.textContent?.trim()).toBe('2.5 h');
    expect(q('triage-taken-at')?.textContent).toContain('2026');
  });

  it('draws both series with one point per day', async () => {
    const { q } = await render(metrics());
    expect(q('unread-path')?.getAttribute('d')?.match(/[ML]/g)).toHaveLength(3);
    expect(q('coverage-path')?.getAttribute('d')?.match(/[ML]/g)).toHaveLength(3);
  });

  it('shows the empty states before the first snapshot', async () => {
    const { q } = await render({ current: null, history: [], llmHours: 0 });
    expect(q('triage-no-snapshot')).not.toBeNull();
    expect(q('triage-no-history')).not.toBeNull();
    expect(q('triage-chart')).toBeNull();
    expect(q('triage-policy')).toBeNull();
  });

  it('Refresh posts a snapshot and shows the metrics it answers with', async () => {
    const { fixture, q } = await render({ current: null, history: [], llmHours: 0 });
    const reply = new Subject<TriageMetricsDto>();
    triage.snapshot.mockReturnValue(reply);
    q('triage-refresh')?.click();
    await fixture.whenStable();
    expect(triage.snapshot).toHaveBeenCalledTimes(1);
    expect((q('triage-refresh') as HTMLButtonElement).disabled).toBe(true);
    reply.next(metrics());
    reply.complete();
    await fixture.whenStable();
    expect(q('triage-policy')?.textContent?.trim()).toBe('42.5%');
    expect((q('triage-refresh') as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows a retryable error when loading fails', async () => {
    const { fixture, q } = await render(metrics());
    triage.get.mockReturnValue(throwError(() => new Error('down')));
    fixture.componentInstance.load();
    await fixture.whenStable();
    expect(q('triage-error')?.textContent).toContain('could not be refreshed');
    expect(q('triage-policy')?.textContent?.trim()).toBe('42.5%');
  });

  it('arrow keys move the chart tooltip from the latest day; Escape hides it', async () => {
    const { fixture, q } = await render(metrics());
    const chart = q('triage-chart')!;
    const press = async (key: string) => {
      chart.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
      await fixture.whenStable();
    };
    await press('ArrowLeft');
    expect(q('chart-tooltip')?.textContent).toContain('Inbox unread: 20');
    expect(q('chart-tooltip')?.textContent).toContain('42.5%');
    await press('ArrowLeft');
    expect(q('chart-tooltip')?.textContent).toContain('Inbox unread: 30');
    await press('Home');
    expect(q('chart-tooltip')?.textContent).toContain('Inbox unread: 40');
    expect(q('chart-tooltip')?.textContent).toContain('10.0%');
    await press('Escape');
    expect(q('chart-tooltip')).toBeNull();
  });
});

describe('TriageService', () => {
  it('reads the metrics and posts a snapshot', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const service = TestBed.inject(TriageService);
    const http = TestBed.inject(HttpTestingController);
    const seen: number[] = [];
    service.get().subscribe((m) => seen.push(m.llmHours));
    service.snapshot().subscribe((m) => seen.push(m.llmHours));
    http.expectOne({ method: 'GET', url: '/api/dashboard/triage' }).flush(metrics({ llmHours: 1 }));
    const post = http.expectOne({ method: 'POST', url: '/api/dashboard/triage/snapshot' });
    expect(post.request.body).toBeNull();
    post.flush(metrics({ llmHours: 2 }));
    expect(seen).toEqual([1, 2]);
    http.verify();
  });
});
