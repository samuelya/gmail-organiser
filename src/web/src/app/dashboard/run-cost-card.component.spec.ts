import { Directive, computed, input, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { BaseChartDirective } from 'ng2-charts';
import { Observable, of, throwError } from 'rxjs';
import { ANALYSIS_RUN_JOB, AnalysisRunDto } from '../analyse/analysis.models';
import { AnalysisService } from '../analyse/analysis.service';
import { ChartPalette } from '../core/charts/chart-theme';
import { JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { RunCostCard, runCostData } from './run-cost-card.component';
import { RUN_COST_FETCH, runCosts } from './run-cost';

const palette: ChartPalette = {
  primary: 'rgb(1, 0, 0)',
  secondary: 'rgb(2, 0, 0)',
  tertiary: 'rgb(3, 0, 0)',
  tertiaryContainer: 'rgb(10, 0, 0)',
  surface: 'rgb(11, 0, 0)',
  error: 'rgb(4, 0, 0)',
  outline: 'rgb(5, 0, 0)',
  grid: 'rgb(6, 0, 0)',
  text: 'rgb(7, 0, 0)',
  tooltipBackground: 'rgb(8, 0, 0)',
  tooltipText: 'rgb(9, 0, 0)',
  fontFamily: 'Synthetic Sans',
};

const run = (over: Partial<AnalysisRunDto> = {}) =>
  ({
    id: 'r1',
    kind: 'analyse',
    scope: 'sender',
    senderAddress: 'news@example.com',
    status: 'completed',
    messagesLlm: 2,
    messagesDerived: 5,
    messagesFromMemory: 3,
    llmSeconds: 30,
    savedPercent: 0.8,
    finishedAt: '2026-03-04T10:15:00Z',
    ...over,
  }) as AnalysisRunDto;

/** jsdom has no canvas; the chart's data and options are read from the component. */
// eslint-disable-next-line @angular-eslint/directive-selector -- stands in for ng2-charts' selector
@Directive({ selector: 'canvas[baseChart]' })
class StubChart {
  readonly type = input<string>();
  readonly data = input<unknown>();
  readonly options = input<unknown>();
}

class FakeJobs {
  readonly jobs = signal<JobDto[]>([]);
  readonly reconnects = signal(0);
  readonly activeJobs = computed(() => this.jobs());
}

const analysisJob = (status: string, progress: number) =>
  ({ id: 'j1', type: ANALYSIS_RUN_JOB, status, progress }) as unknown as JobDto;

describe('run cost chart data', () => {
  it('stacks model, derived and memory and draws model seconds as a dashed line on the right', () => {
    const data = runCostData(runCosts([run()]), palette, 'en-US');
    expect(data.labels).toHaveLength(1);
    expect(
      data.datasets.map((d) => [d.type, d.label, d.data, d.backgroundColor, d.yAxisID]),
    ).toEqual([
      ['bar', 'By model', [2], 'rgb(1, 0, 0)', 'y'],
      ['bar', 'Derived', [5], 'rgb(3, 0, 0)', 'y'],
      ['bar', 'From memory', [3], 'rgb(2, 0, 0)', 'y'],
      ['line', 'Model seconds', [30], 'rgb(4, 0, 0)', 'y1'],
    ]);
    expect((data.datasets[3] as { borderDash: number[] }).borderDash).toEqual([6, 4]);
  });
});

describe('RunCostCard', () => {
  let listRuns: ReturnType<typeof vi.fn>;
  let jobs: FakeJobs;

  async function render(reply: Observable<AnalysisRunDto[]>) {
    listRuns = vi.fn(() => reply);
    jobs = new FakeJobs();
    TestBed.configureTestingModule({
      providers: [
        { provide: AnalysisService, useValue: { listRuns } },
        { provide: JobsService, useValue: jobs },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    TestBed.overrideComponent(RunCostCard, {
      remove: { imports: [BaseChartDirective] },
      add: { imports: [StubChart] },
    });
    const fixture = TestBed.createComponent(RunCostCard);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q, card: fixture.componentInstance };
  }

  it('asks for finished runs and labels the chart with the summary', async () => {
    const { q } = await render(of([run()]));
    expect(listRuns).toHaveBeenCalledWith(false, RUN_COST_FETCH);
    expect(q('chart-canvas')?.getAttribute('aria-label')).toBe(
      'In the last run, 80% of messages needed no model call; model time 30 s.',
    );
  });

  it('tooltip names the run target and its saving', async () => {
    const { card } = await render(of([run()]));
    const callbacks = card.options().plugins?.tooltip?.callbacks as unknown as {
      title: (items: { dataIndex: number }[]) => string[];
      footer: (items: { dataIndex: number }[]) => string;
    };
    expect(callbacks.title([{ dataIndex: 0 }])[1]).toBe('Sender: news@example.com');
    expect(callbacks.footer([{ dataIndex: 0 }])).toBe('Saved: 80%');
  });

  it('reloads when an analysis job changes status', async () => {
    const { fixture } = await render(of([run()]));
    jobs.jobs.set([analysisJob('running', 10)]);
    await fixture.whenStable();
    jobs.jobs.set([analysisJob('running', 50)]);
    await fixture.whenStable();
    expect(listRuns).toHaveBeenCalledTimes(2);

    jobs.jobs.set([analysisJob('completed', 100)]);
    await fixture.whenStable();
    expect(listRuns).toHaveBeenCalledTimes(3);
  });

  it('shows the empty text when only compare runs exist', async () => {
    const { q } = await render(of([run({ kind: 'compare' })]));
    expect(q('chart-empty')?.textContent).toContain('No analysis runs yet.');
  });

  it('shows a retryable error', async () => {
    const { fixture, el, q } = await render(throwError(() => new Error('down')));
    expect(q('run-cost-error')?.textContent).toContain('could not be loaded');

    listRuns.mockReturnValue(of([run()]));
    el.querySelector<HTMLButtonElement>('[data-testid="run-cost-error"] button')?.click();
    await fixture.whenStable();
    expect(q('run-cost-error')).toBeNull();
    expect(q('chart-canvas')).not.toBeNull();
  });
});
