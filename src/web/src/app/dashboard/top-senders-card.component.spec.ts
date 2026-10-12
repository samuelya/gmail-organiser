import { ChangeDetectionStrategy, Component, Directive, input, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import { BaseChartDirective } from 'ng2-charts';
import { Observable, of, throwError } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { ChartPalette } from '../core/charts/chart-theme';
import { SenderDto } from '../senders/senders.models';
import { SendersService } from '../senders/senders.service';
import { TopSendersCard, topSendersData } from './top-senders-card.component';
import { TOP_SENDERS_QUERY, topSenders } from './top-senders';

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

const sender = (address: string, total: number, unread: number, displayName: string | null) =>
  ({ address, displayName, totalCount: total, unreadCount: unread }) as SenderDto;

const page = (items: SenderDto[]) => ({ items, page: 1, pageSize: 10, total: items.length });

/** jsdom has no canvas; the chart's data and options are read from the component. */
// eslint-disable-next-line @angular-eslint/directive-selector -- stands in for ng2-charts' selector
@Directive({ selector: 'canvas[baseChart]' })
class StubChart {
  readonly type = input<string>();
  readonly data = input<unknown>();
  readonly options = input<unknown>();
}

@Component({
  imports: [TopSendersCard],
  template: `<app-top-senders-card [messagesStored]="stored()" />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly stored = signal<number | null>(null);
}

describe('top senders chart data', () => {
  it('stacks unread in primary and read in outline, one bar per sender', () => {
    const rows = topSenders([
      sender('a@example.com', 10, 4, 'Alpha'),
      sender('b@example.com', 6, 0, null),
    ]);
    const data = topSendersData(rows, palette);
    expect(data.labels).toEqual(['Alpha', 'b@example.com']);
    expect(data.datasets.map((d) => [d.label, d.data, d.backgroundColor, d.stack])).toEqual([
      ['Unread', [4, 0], 'rgb(1, 0, 0)', 'mail'],
      ['Read', [6, 6], 'rgb(5, 0, 0)', 'mail'],
    ]);
  });
});

describe('TopSendersCard', () => {
  let list: ReturnType<typeof vi.fn>;

  async function render(reply: Observable<PagedDto<SenderDto>>) {
    list = vi.fn(() => reply);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SendersService, useValue: { list } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    TestBed.overrideComponent(TopSendersCard, {
      remove: { imports: [BaseChartDirective] },
      add: { imports: [StubChart] },
    });
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const card = fixture.debugElement.children[0].componentInstance as TopSendersCard;
    return { fixture, el, q, card };
  }

  it('loads the top ten and labels the chart with the summary', async () => {
    const { fixture, q } = await render(of(page([sender('a@example.com', 30, 10, 'Alpha')])));
    expect(list).toHaveBeenCalledWith(TOP_SENDERS_QUERY);
    expect(q('chart-canvas')?.getAttribute('aria-label')).toBe('The top sender sent 30 messages.');

    fixture.componentInstance.stored.set(120);
    await fixture.whenStable();
    expect(list).toHaveBeenCalledTimes(2);
    expect(q('chart-canvas')?.getAttribute('aria-label')).toBe(
      'The top sender sent 30 of your 120 stored messages (25%).',
    );
  });

  it('table rows link to the sender on the Senders page', async () => {
    const { fixture, q } = await render(of(page([sender('a+b@example.com', 3, 1, 'Alpha')])));
    q('chart-toggle')?.click();
    await fixture.whenStable();
    const link = q('chart-table')?.querySelector('a');
    expect(link?.textContent?.trim()).toBe('Alpha');
    expect(link?.getAttribute('href')).toBe('/senders?search=a%2Bb@example.com');
  });

  it('clicking a bar opens that sender', async () => {
    const { card } = await render(
      of(page([sender('a@example.com', 3, 1, null), sender('b@example.com', 2, 0, null)])),
    );
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const onClick = card.options().onClick as (e: unknown, els: { index: number }[]) => void;
    onClick({}, [{ index: 1 }]);
    expect(navigate).toHaveBeenCalledWith(['/senders'], {
      queryParams: { search: 'b@example.com' },
    });
  });

  it('shows the empty text when there are no senders', async () => {
    const { q } = await render(of(page([])));
    expect(q('chart-empty')?.textContent).toContain('No senders yet; fetch your mailbox first.');
  });

  it('shows a retryable error', async () => {
    const { fixture, el, q } = await render(throwError(() => new Error('down')));
    expect(q('top-senders-error')?.textContent).toContain('could not be loaded');

    list.mockReturnValue(of(page([sender('a@example.com', 3, 1, null)])));
    el.querySelector<HTMLButtonElement>('[data-testid="top-senders-error"] button')?.click();
    await fixture.whenStable();
    expect(q('top-senders-error')).toBeNull();
    expect(q('chart-canvas')).not.toBeNull();
  });
});
