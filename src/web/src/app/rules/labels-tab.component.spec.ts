import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { JobDto } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { LabelDto } from '../review/labels.models';
import { LabelsService } from '../review/labels.service';
import { planItem } from './label-plan-item.component.spec';
import { LabelPlanDto } from './label-plan.models';
import { LabelsTab } from './labels-tab.component';
import { RulesService } from './rules.service';

const labels: LabelDto[] = [
  { id: 'L1', name: 'Topic-Alpha', type: 'user' },
  { id: 'L2', name: 'Old', type: 'user' },
];

const plan = (over: Partial<LabelPlanDto> = {}): LabelPlanDto => ({
  id: 'p-1',
  status: 'draft',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  labelCount: 2,
  warnings: [],
  items: [
    planItem(),
    planItem({ id: 'i-2', kind: 'empty', labelId: 'L2', labelName: 'Old', proposedName: null }),
  ],
  jobId: null,
  ...over,
});

const job = (over: Partial<JobDto> = {}): JobDto => ({
  id: 'j-1',
  type: 'label_plan_apply',
  queue: 'rules',
  status: 'running',
  progress: { done: 1, total: 2, message: 'Old' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

describe('LabelsTab', () => {
  let rules: Record<string, ReturnType<typeof vi.fn>>;
  let jobs: Map<string, JobDto>;
  let held: ReturnType<typeof signal<ReadonlyMap<string, JobDto>>>;
  let confirm: boolean;
  let dialogData: unknown[];

  async function render(latest: () => ReturnType<RulesService['latestPlan']>) {
    jobs = new Map();
    held = signal<ReadonlyMap<string, JobDto>>(jobs);
    confirm = true;
    dialogData = [];
    rules = {
      latestPlan: vi.fn(latest),
      createPlan: vi.fn(() => of(plan({ id: 'p-2' }))),
      updateItem: vi.fn(() => of(plan())),
      applyPlan: vi.fn(() => of({ jobId: 'j-1' })),
      discardPlan: vi.fn(() => of(plan({ status: 'discarded' }))),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: RulesService, useValue: rules },
        {
          provide: LabelsService,
          useValue: { labels: () => of(labels), refresh: vi.fn(() => of(labels)) },
        },
        {
          provide: JobsService,
          useValue: {
            job: (id: string) => held().get(id),
            reconnects: signal(0),
            cancel: vi.fn(() => of(undefined)),
          },
        },
        {
          provide: MatDialog,
          useValue: {
            open: (_: unknown, config: { data: unknown }) => {
              dialogData.push(config.data);
              return { afterClosed: () => of(confirm) };
            },
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(LabelsTab);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends HTMLElement = HTMLElement>(id: string) =>
      el.querySelector<T>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    return { fixture, el, q, all };
  }

  const notFound = () =>
    throwError(() => new HttpErrorResponse({ status: 404, statusText: 'Not Found' }));

  it('shows the empty state on 404 and builds a plan without confirming', async () => {
    const { fixture, q, all } = await render(notFound);
    expect(q('plan-empty')).not.toBeNull();
    expect(q('plan-error')).toBeNull();
    q('plan-review')!.click();
    await fixture.whenStable();
    expect(dialogData).toEqual([]);
    expect(rules['createPlan']).toHaveBeenCalled();
    expect(q('plan-empty')).toBeNull();
    expect(all('plan-group').map((g) => g.querySelector('h3')!.textContent!.trim())).toEqual([
      'Empty labels (1)',
      'Flat → nested (1)',
    ]);
  });

  it('confirms before replacing a draft, and shows the warnings', async () => {
    const { fixture, q } = await render(() => of(plan({ warnings: ['Synthetic warning'] })));
    expect(q('plan-warnings')!.textContent).toContain('Synthetic warning');
    confirm = false;
    q('plan-review')!.click();
    await fixture.whenStable();
    expect(dialogData).toHaveLength(1);
    expect(rules['createPlan']).not.toHaveBeenCalled();
  });

  it('shows the load error with a retry', async () => {
    const { q } = await render(() =>
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server Error' })),
    );
    expect(q('plan-error')).not.toBeNull();
    expect(q('plan-empty')).toBeNull();
  });

  it('patches an item decision and shows the returned plan', async () => {
    const { fixture, el, q } = await render(() => of(plan()));
    const accepted = plan({
      items: [planItem({ status: 'accepted' }), plan().items[1]],
    });
    rules['updateItem'].mockReturnValue(of(accepted));
    expect(q<HTMLButtonElement>('plan-apply')!.disabled).toBe(true);
    el.querySelector<HTMLButtonElement>(
      '[data-kind="nest"] [data-testid="plan-item-accept"] button',
    )!.click();
    await fixture.whenStable();
    expect(rules['updateItem']).toHaveBeenCalledWith('p-1', 'i-1', { status: 'accepted' });
    expect(q('plan-apply')!.textContent).toContain('Apply accepted (1)');
  });

  it('runs item edits one at a time so the last response holds both', async () => {
    const { fixture, el } = await render(() => of(plan()));
    const first = new Subject<LabelPlanDto>();
    const second = new Subject<LabelPlanDto>();
    rules['updateItem'].mockReturnValueOnce(first).mockReturnValueOnce(second);
    const accept = (kind: string) =>
      el
        .querySelector<HTMLButtonElement>(
          `[data-kind="${kind}"] [data-testid="plan-item-accept"] button`,
        )!
        .click();
    accept('nest');
    accept('empty');
    await fixture.whenStable();
    expect(rules['updateItem']).toHaveBeenCalledTimes(1);

    first.next(plan({ items: [planItem({ status: 'accepted' }), plan().items[1]] }));
    first.complete();
    await fixture.whenStable();
    expect(rules['updateItem']).toHaveBeenCalledTimes(2);
    expect(rules['updateItem']).toHaveBeenLastCalledWith('p-1', 'i-2', { status: 'accepted' });
    second.next(
      plan({
        items: [planItem({ status: 'accepted' }), { ...plan().items[1], status: 'accepted' }],
      }),
    );
    second.complete();
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="plan-apply"]')!.textContent).toContain(
      'Apply accepted (2)',
    );
  });

  it('reloads the plan once a cancel returns, even after the job already ended', async () => {
    const applying = plan({ status: 'applying', jobId: 'j-1' });
    const { fixture, q } = await render(() => of(applying));
    held.set(new Map([['j-1', job({ status: 'paused' })]]));
    await fixture.whenStable();
    const cancel = new Subject<void>();
    vi.mocked(TestBed.inject(JobsService).cancel).mockReturnValue(cancel);
    q('plan-cancel')!.click();
    await fixture.whenStable();
    expect(q<HTMLButtonElement>('plan-cancel')!.disabled).toBe(true);

    // The cancelled event lands first and its reload still sees the plan applying.
    held.set(new Map([['j-1', job({ status: 'cancelled', version: 2 })]]));
    await fixture.whenStable();
    rules['latestPlan'].mockClear();
    rules['latestPlan'].mockImplementation(() => of(plan()));
    cancel.next();
    cancel.complete();
    await fixture.whenStable();
    expect(rules['latestPlan']).toHaveBeenCalledTimes(1);
    expect(q('plan-progress')).toBeNull();
    expect(q<HTMLButtonElement>('plan-review')!.disabled).toBe(false);
  });

  it('applies, follows the job progress and reloads the plan when it ends', async () => {
    const draft = plan({ items: [planItem({ status: 'accepted' }), plan().items[1]] });
    const applying = plan({ ...draft, status: 'applying', jobId: 'j-1' });
    const latest = new Subject<LabelPlanDto>();
    const { fixture, q } = await render(() => of(draft));
    rules['latestPlan'].mockImplementation(() => latest);

    q('plan-apply')!.click();
    await fixture.whenStable();
    expect(dialogData).toEqual([{ empty: 0, near_duplicate: 0, nest: 1 }]);
    expect(rules['applyPlan']).toHaveBeenCalledWith('p-1');
    latest.next(applying);
    held.set(new Map([['j-1', job()]]));
    await fixture.whenStable();
    expect(q('plan-progress')!.textContent).toContain('Applying 1 of 2');
    expect(q('plan-progress')!.textContent).toContain('Old');

    q('plan-cancel')!.click();
    expect(TestBed.inject(JobsService).cancel).toHaveBeenCalledWith('j-1');

    rules['latestPlan'].mockClear();
    held.set(new Map([['j-1', job({ status: 'completed', version: 2 })]]));
    await fixture.whenStable();
    expect(rules['latestPlan']).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(LabelsService).refresh).toHaveBeenCalled();
    latest.next(
      plan({
        ...applying,
        status: 'applied',
        items: [planItem({ status: 'failed', error: 'Gmail refused' }), plan().items[1]],
      }),
    );
    await fixture.whenStable();
    expect(q('plan-progress')).toBeNull();
    expect(q('plan-failed')!.textContent).toContain('1 change failed');
    expect(q('plan-item-error')!.textContent).toContain('Gmail refused');
    expect(q('plan-apply')).toBeNull();
    expect(q('plan-discard')).toBeNull();
  });

  it('discards after confirming and shows the empty state', async () => {
    const { fixture, q } = await render(() => of(plan()));
    rules['latestPlan'].mockImplementation(notFound);
    q('plan-discard')!.click();
    await fixture.whenStable();
    expect(dialogData).toHaveLength(1);
    expect(rules['discardPlan']).toHaveBeenCalledWith('p-1');
    expect(q('plan-empty')).not.toBeNull();
  });

  it('shows the label tree with the proposals inline', async () => {
    const { fixture, q, all } = await render(() => of(plan()));
    q('plan-view-tree')!.querySelector('button')!.click();
    await fixture.whenStable();
    const rows = all('plan-tree-row');
    expect(rows.map((r) => r.dataset['path'])).toEqual(['Old', 'Topic-Alpha']);
    expect(rows[0].querySelector('.line-through')).not.toBeNull();
    expect(rows[1].textContent).toContain('→ Topic/Alpha');
  });
});
