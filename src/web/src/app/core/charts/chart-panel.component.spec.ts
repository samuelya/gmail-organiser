import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ChartPanel } from './chart-panel.component';
import { ChartTable } from './chart.models';

@Component({
  imports: [ChartPanel],
  template: `
    <app-chart-panel
      heading="Synthetic chart"
      summary="Two synthetic rows."
      [table]="table()"
      emptyText="No data yet."
    >
      <ng-template><span data-testid="projected">chart</span></ng-template>
    </app-chart-panel>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly table = signal<ChartTable>({
    columns: ['Name', 'Count'],
    rows: [
      ['Alpha', 1200],
      ['Beta', 3],
    ],
  });
}

describe('ChartPanel', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q };
  }

  it('labels the chart template with the summary and offers a table toggle', async () => {
    const { el, q } = await render();

    expect(el.querySelector('h3')!.textContent).toContain('Synthetic chart');
    const canvas = q('chart-canvas')!;
    expect(canvas.getAttribute('role')).toBe('img');
    expect(canvas.getAttribute('aria-label')).toBe('Two synthetic rows.');
    expect(canvas.hidden).toBe(false);
    expect(q('projected')).not.toBeNull();
    expect(q('chart-table')).toBeNull();
    expect(q('chart-toggle')!.textContent).toContain('Show table');
    expect(q('chart-toggle')!.getAttribute('aria-pressed')).toBe('false');
  });

  it('swaps the chart for a table with column headers and back', async () => {
    const { fixture, q } = await render();

    q('chart-toggle')!.click();
    await fixture.whenStable();

    const table = q('chart-table')!;
    const headers = [...table.querySelectorAll('th')];
    expect(headers.map((th) => th.textContent!.trim())).toEqual(['Name', 'Count']);
    expect(headers.every((th) => th.getAttribute('scope') === 'col')).toBe(true);
    const cells = [...table.querySelectorAll('tbody td')].map((td) => td.textContent!.trim());
    expect(cells).toEqual(['Alpha', '1,200', 'Beta', '3']);
    expect(q('chart-canvas')!.hidden).toBe(true);
    expect(q('chart-toggle')!.textContent).toContain('Show chart');
    expect(q('chart-toggle')!.getAttribute('aria-pressed')).toBe('true');

    q('chart-toggle')!.click();
    await fixture.whenStable();
    expect(q('chart-table')).toBeNull();
    expect(q('chart-canvas')!.hidden).toBe(false);
  });

  it('shows the empty text and no chart or toggle without rows', async () => {
    const { fixture, q } = await render();
    fixture.componentInstance.table.set({ columns: ['Name', 'Count'], rows: [] });
    await fixture.whenStable();

    expect(q('chart-empty')!.textContent).toContain('No data yet.');
    expect(q('chart-canvas')).toBeNull();
    expect(q('projected')).toBeNull();
    expect(q('chart-toggle')).toBeNull();
  });
});
