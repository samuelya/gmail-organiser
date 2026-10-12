import { ChartPalette } from '../core/charts/chart-theme';
import {
  statusChartData,
  statusChartSummary,
  statusChartTable,
} from './analysis-status-chart.component';
import { AnalysisSummaryDto } from './analysis.models';

const summary = {
  notAnalysed: 1200,
  analysed: 300,
  approved: 40,
  rejected: 2,
  applied: 25,
} as AnalysisSummaryDto;

const empty = {
  notAnalysed: 0,
  analysed: 0,
  approved: 0,
  rejected: 0,
  applied: 0,
} as AnalysisSummaryDto;

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
  tertiaryContainer: 'rgb(10, 0, 0)',
  surface: 'rgb(11, 0, 0)',
  fontFamily: 'Synthetic Sans',
};

describe('analysis status chart', () => {
  it('builds one stacked bar with a coloured segment per status', () => {
    const data = statusChartData(summary, palette);

    expect(data.labels).toEqual(['Messages']);
    expect(data.datasets.map((d) => [d.label, d.data, d.backgroundColor, d.stack])).toEqual([
      ['Not analysed', [1200], 'rgb(5, 0, 0)', 'status'],
      ['Waiting for review', [300], 'rgb(3, 0, 0)', 'status'],
      ['Approved', [40], 'rgb(2, 0, 0)', 'status'],
      ['Rejected', [2], 'rgb(4, 0, 0)', 'status'],
      ['Applied', [25], 'rgb(1, 0, 0)', 'status'],
    ]);
  });

  it('builds the table and the summary sentence from the same counts', () => {
    expect(statusChartTable(summary)).toEqual({
      columns: ['Status', 'Messages'],
      rows: [
        ['Not analysed', 1200],
        ['Waiting for review', 300],
        ['Approved', 40],
        ['Rejected', 2],
        ['Applied', 25],
      ],
    });
    expect(statusChartSummary(summary, 'en-US')).toBe(
      'Messages by analysis status: 1,200 not analysed, 300 waiting for review, 40 approved, 2 rejected, 25 applied.',
    );
  });

  it('has no table rows when nothing has been fetched', () => {
    expect(statusChartTable(empty).rows).toEqual([]);
  });
});
