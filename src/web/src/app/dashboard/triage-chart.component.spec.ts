import { TestBed } from '@angular/core/testing';
import { ChartPalette } from '../core/charts/chart-theme';
import { TriageChart, triageChartData } from './triage-chart.component';
import { TriagePoint } from './triage.models';

const palette: ChartPalette = {
  primary: 'rgb(1, 0, 0)',
  secondary: 'rgb(2, 0, 0)',
  tertiary: 'rgb(3, 0, 0)',
  error: 'rgb(4, 0, 0)',
  outline: 'rgb(5, 0, 0)',
  grid: 'rgb(6, 0, 0)',
  text: 'rgb(7, 0, 0)',
  tooltipBackground: 'rgb(8, 0, 0)',
  tooltipText: 'rgb(9, 0, 0)',
  fontFamily: 'Synthetic Sans',
};

const points: TriagePoint[] = [
  { day: '2026-01-01', unread: 40, policyCoverage: 10, filterCoverage: 0 },
  { day: '2026-01-02', unread: 20, policyCoverage: 42.5, filterCoverage: 5 },
];

describe('triage chart', () => {
  it('builds three unfilled series, unread on the left axis and coverage on the right', () => {
    const data = triageChartData(points, palette, 'en-US');

    expect(data.labels).toEqual(['1 Jan', '2 Jan']);
    expect(
      data.datasets.map((d) => [d.label, d.data, d.yAxisID, d.borderColor, d.fill, d.pointRadius]),
    ).toEqual([
      ['Inbox unread', [40, 20], 'y', 'rgb(1, 0, 0)', false, 0],
      ['% covered by policy', [10, 42.5], 'y1', 'rgb(3, 0, 0)', false, 0],
      ['% covered by filter', [0, 5], 'y1', 'rgb(2, 0, 0)', false, 0],
    ]);
  });

  it('dashes the filter line so it stays distinct from unread', () => {
    const data = triageChartData(points, palette, 'en-US');
    expect(data.datasets.map((d) => d.borderDash)).toEqual([[], [], [6, 4]]);
  });

  it('draws a lone day as dots', () => {
    const data = triageChartData(points.slice(0, 1), palette, 'en-US');
    expect(data.datasets.every((d) => d.pointRadius === 3)).toBe(true);
  });

  it('shows the empty text and no canvas without history', async () => {
    const fixture = TestBed.createComponent(TriageChart);
    fixture.componentRef.setInput('points', []);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="chart-empty"]')?.textContent).toContain(
      'The chart fills in once snapshots exist',
    );
    expect(el.querySelector('canvas')).toBeNull();
  });

  it('asks Chart.js for an index tooltip across all three series', () => {
    const chart = TestBed.createComponent(TriageChart).componentInstance;
    expect(chart.options().interaction).toEqual({ mode: 'index', intersect: false });
  });

  it('titles the tooltip with the full date, year included', () => {
    const fixture = TestBed.createComponent(TriageChart);
    fixture.componentRef.setInput('points', points);
    const title = fixture.componentInstance.options().plugins?.tooltip?.callbacks?.title as (
      items: { dataIndex: number }[],
    ) => string;
    expect(title([{ dataIndex: 1 }])).toBe('Jan 2, 2026');
  });
});
