import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import type { ActiveElement } from 'chart.js';
import { of, Subject, throwError } from 'rxjs';
import { ANALYSIS_RUN_JOB } from '../analyse/analysis.models';
import { ChartTheme } from '../core/charts/chart-theme';
import { JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { MailboxBreakdownDto } from './breakdown.models';
import { BreakdownService } from './breakdown.service';
import { BAR_ROW_HEIGHT, MailboxMixCard } from './mailbox-mix-card.component';

const breakdown: MailboxBreakdownDto = {
  categories: { primary: 30, promotions: 48, social: 2, updates: 15, forums: 5 },
  mailTypes: [
    { mailType: 'newsletter', count: 120 },
    { mailType: 'receipt', count: 0 },
  ],
  unclassified: 4,
};

const empty: MailboxBreakdownDto = {
  categories: { primary: 0, promotions: 0, social: 0, updates: 0, forums: 0 },
  mailTypes: [{ mailType: 'newsletter', count: 0 }],
  unclassified: 0,
};

class FakeJobs {
  readonly jobs = signal<JobDto[]>([]);
  readonly reconnects = signal(0);
  readonly activeJobs = computed(() => this.jobs());
}

describe('MailboxMixCard', () => {
  async function render(get: () => ReturnType<BreakdownService['get']>) {
    const api = { get: vi.fn(get) };
    const jobs = new FakeJobs();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: BreakdownService, useValue: api },
        { provide: JobsService, useValue: jobs },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(MailboxMixCard);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { fixture, el, q, api, jobs };
  }

  it('shows a progress bar until the breakdown arrives', async () => {
    const pending = new Subject<MailboxBreakdownDto>();
    const { fixture, el, q } = await render(() => pending);

    expect(el.querySelector('mat-progress-bar')).not.toBeNull();

    pending.next(breakdown);
    await fixture.whenStable();
    expect(el.querySelector('mat-progress-bar')).toBeNull();
    expect(q('mailbox-mix-categories')).not.toBeNull();
  });

  it('labels both charts with their summaries', async () => {
    const { el } = await render(() => of(breakdown));

    const labels = [...el.querySelectorAll('[data-testid="chart-canvas"]')].map((c) =>
      c.getAttribute('aria-label'),
    );
    expect(labels).toEqual([
      'Most of your mail is in Promotions (48%).',
      'Newsletter is the most common mail type (120 of 120 classified, not counting rejected).',
    ]);
  });

  it('paints slices in neutral categorical tones with surface-coloured gaps', async () => {
    const { fixture } = await render(() => of(breakdown));
    const p = TestBed.inject(ChartTheme).palette();

    const [slices] = fixture.componentInstance.categoryData().datasets;
    expect(slices.borderColor).toBe(p.surface);
    expect(slices.backgroundColor).toEqual([
      p.primary,
      p.secondary,
      p.tertiary,
      p.tertiaryContainer,
      p.outline,
    ]);
  });

  it('shows each empty text when everything is zero', async () => {
    const { el } = await render(() => of(empty));

    const texts = [...el.querySelectorAll('[data-testid="chart-empty"]')].map((p) =>
      p.textContent!.trim(),
    );
    expect(texts).toEqual(['No mail fetched yet.', 'No suggestions yet.']);
  });

  it('shows the error with a Retry that reloads', async () => {
    let fail = true;
    const { fixture, q, api } = await render(() =>
      fail ? throwError(() => new Error('down')) : of(breakdown),
    );

    expect(q('mailbox-mix-error')!.textContent).toContain('could not be loaded');

    fail = false;
    q('mailbox-mix-error')!.querySelector('button')!.click();
    await fixture.whenStable();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(q('mailbox-mix-error')).toBeNull();
    expect(q('mailbox-mix-mail-types')).not.toBeNull();
  });

  it('reloads when a job changes status', async () => {
    const { fixture, api, jobs } = await render(() => of(breakdown));

    jobs.jobs.set([{ id: 'j1', type: ANALYSIS_RUN_JOB, status: 'running' } as JobDto]);
    await fixture.whenStable();
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('opens the review page filtered to a clicked bar type; Unclassified goes nowhere', async () => {
    const { fixture } = await render(() => of(breakdown));
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const card = fixture.componentInstance;
    const at = (index: number) => [{ index } as ActiveElement];

    card.openBar(at(0));
    expect(navigate).toHaveBeenCalledWith(['/review'], {
      queryParams: { mailType: 'newsletter' },
    });

    navigate.mockClear();
    card.openBar(at(1));
    card.openBar([]);
    expect(navigate).not.toHaveBeenCalled();
    expect(card.barHeight()).toBe(2 * BAR_ROW_HEIGHT + 40);
  });

  it('links the table rows the same way', async () => {
    const { fixture, q } = await render(() => of(breakdown));

    const panel = q('mailbox-mix-mail-types')!;
    panel.querySelector<HTMLElement>('[data-testid="chart-toggle"]')!.click();
    await fixture.whenStable();

    const link = panel.querySelector('table a')!;
    expect(link.textContent!.trim()).toBe('newsletter');
    expect(link.getAttribute('href')).toBe('/review?mailType=newsletter');
    expect(panel.querySelectorAll('table a').length).toBe(1);
  });
});
