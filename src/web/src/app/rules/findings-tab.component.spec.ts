import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { Observable, of, throwError } from 'rxjs';
import { FindingsTab } from './findings-tab.component';
import { FilterDto, FilterFindingDto, FilterReviewDto } from './rules.models';
import { RulesService } from './rules.service';

const filterDto = (id: string, from: string): FilterDto => ({
  id,
  criteria: {
    from,
    to: null,
    subject: null,
    query: null,
    negatedQuery: null,
    hasAttachment: null,
    excludeChats: null,
    size: null,
    sizeComparison: null,
  },
  criteriaSummary: `from:${from}`,
  action: {
    addLabels: [{ id: 'L1', name: 'Topic/Alpha' }],
    removeLabelIds: [],
    skipInbox: false,
    markRead: false,
    forwards: false,
  },
  createdByApp: false,
  firstSeenAt: '2026-01-01T00:00:00Z',
  deletedAt: null,
  deletedByApp: false,
  restoredFrom: null,
});

const finding = (over: Partial<FilterFindingDto> = {}): FilterFindingDto => ({
  id: 'k1',
  kind: 'duplicate',
  filterIds: ['f1', 'f2'],
  filters: [filterDto('f1', 'a@example.com'), filterDto('f2', 'a@example.com')],
  description: 'Two filters do the same thing.',
  fix: { kind: 'delete', deleteFilterIds: ['f2'], create: null },
  status: 'open',
  appliedAt: null,
  error: null,
  reviewId: 'r1',
  ...over,
});

const reviewDto = (findings: FilterFindingDto[], over: Partial<FilterReviewDto> = {}) =>
  ({
    id: 'r1',
    createdAt: new Date().toISOString(),
    filterCount: 4,
    findings,
    summary: null,
    summaryModel: null,
    summarisedAt: null,
    summaryError: null,
    ...over,
  }) satisfies FilterReviewDto;

const standard = () =>
  reviewDto([
    finding(),
    finding({ id: 'k2', kind: 'mergeable', description: 'Mergeable senders.' }),
    finding({ id: 'k3', kind: 'mergeable', description: 'More mergeable senders.' }),
    finding({
      id: 'k4',
      kind: 'overlap',
      fix: { kind: 'none', deleteFilterIds: [], create: null },
      description: 'Overlap with a forwarding filter.',
    }),
    finding({ id: 'k5', kind: 'deleted_label', status: 'dismissed' }),
    finding({ id: 'k6', kind: 'deleted_label', reviewId: 'r0', description: 'Half applied.' }),
  ]);

const conflict = (detail: string) =>
  new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail } });

describe('FindingsTab', () => {
  let api: Record<
    'latestReview' | 'startReview' | 'applyFinding' | 'dismissFinding' | 'summarise',
    ReturnType<typeof vi.fn>
  >;

  async function render(latest: Observable<FilterReviewDto | null> = of(standard())) {
    api = {
      latestReview: vi.fn(() => latest),
      startReview: vi.fn(() => of(reviewDto([finding()], { id: 'r2' }))),
      applyFinding: vi.fn((id: string) =>
        of(finding({ id, status: 'applied', appliedAt: new Date().toISOString() })),
      ),
      dismissFinding: vi.fn((id: string) => of(finding({ id, status: 'dismissed' }))),
      summarise: vi.fn(() =>
        of(
          reviewDto([], {
            summary: 'Apply the duplicate fix first.',
            summaryModel: 'synthetic-model',
            summarisedAt: new Date().toISOString(),
          }),
        ),
      ),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: RulesService, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(FindingsTab);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string, root: ParentNode = el) => [
      ...root.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`),
    ];
    const card = (description: string) =>
      all('finding').find((c) => c.textContent!.includes(description))!;
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, el, q, all, card, settle };
  }

  const dialog = (testId: string) =>
    document.querySelector<HTMLElement>(`mat-dialog-container [data-testid="${testId}"]`);

  afterEach(() =>
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = '')),
  );

  it('shows the empty state with the button when there is no review', async () => {
    const { q, settle } = await render(of(null));
    expect(q('findings-empty')).not.toBeNull();
    expect(q('findings-summarise')).toBeNull();
    q('findings-review')!.click();
    await settle();
    expect(api.startReview).toHaveBeenCalled();
    expect(q('findings-empty')).toBeNull();
    expect(q('findings-status')!.textContent).toContain('1 open');
  });

  it('groups open findings by kind with counts and folds the resolved ones', async () => {
    const { q, all } = await render();
    expect(q('findings-status')!.textContent).toMatch(/4\s+filters: 5 open,\s+1 resolved/);
    expect(all('finding-group').map((g) => g.querySelector('h3')!.textContent!.trim())).toEqual([
      'Duplicates (1)',
      'Overlaps (1)',
      'Deleted labels (1)',
      'Mergeable (2)',
    ]);
    expect(q('findings-resolved')!.textContent).toContain('Resolved (1)');
  });

  it('renders the fix, hides Apply for report-only findings and marks carried-over ones', async () => {
    const { all, card } = await render();
    const dup = card('Two filters do the same thing.');
    expect(all('fix-delete', dup)[0].textContent).toContain('from:a@example.com');
    expect(all('finding-apply', dup)[0].textContent!.trim()).toBe('Apply fix');
    const overlap = card('Overlap with a forwarding filter.');
    expect(all('finding-report-only', overlap)).toHaveLength(1);
    expect(all('finding-apply', overlap)).toHaveLength(0);
    const carried = card('Half applied.');
    expect(all('finding-earlier', carried)).toHaveLength(1);
    expect(all('finding-apply', carried)[0].textContent!.trim()).toBe('Resume fix');
  });

  it('applies a fix after confirming and updates that finding in place', async () => {
    const { q, all, card, settle } = await render();
    all('finding-apply', card('Two filters do the same thing.'))[0].click();
    await settle();
    dialog('confirm-ok')!.click();
    // The dialog closes after its exit animation frame.
    await settle();
    await settle();
    expect(api.applyFinding).toHaveBeenCalledWith('k1');
    expect(q('findings-status')!.textContent).toMatch(/4 open,\s+2 resolved/);
    expect(all('finding-group')[0].querySelector('h3')!.textContent).toContain('Overlaps');
  });

  it('keeps a failed fix open with the error and Apply fix', async () => {
    const { all, card, settle } = await render();
    api.applyFinding.mockReturnValue(throwError(() => conflict('Gmail refused the filter.')));
    all('finding-apply', card('Two filters do the same thing.'))[0].click();
    await settle();
    dialog('confirm-ok')!.click();
    // The dialog closes after its exit animation frame.
    await settle();
    await settle();
    const dup = card('Two filters do the same thing.');
    expect(all('finding-error', dup)[0].textContent).toContain('Gmail refused the filter.');
    expect(all('finding-apply', dup)).toHaveLength(1);
  });

  it('dismisses a finding', async () => {
    const { q, all, card, settle } = await render();
    all('finding-dismiss', card('Mergeable senders.'))[0].click();
    await settle();
    expect(api.dismissFinding).toHaveBeenCalledWith('k2');
    expect(q('findings-status')!.textContent).toMatch(/4 open,\s+2 resolved/);
  });

  it('shows the summary with its model and keeps the findings', async () => {
    const { q, all, settle } = await render();
    q('findings-summarise')!.click();
    await settle();
    expect(api.summarise).toHaveBeenCalledWith('r1');
    expect(q('summary')!.textContent).toContain('Apply the duplicate fix first.');
    expect(q('summary-meta')!.textContent).toContain('synthetic-model');
    expect(all('finding-group')).toHaveLength(4);
  });

  it('shows a summary error inline and 409 as nothing to summarise', async () => {
    const { q, settle } = await render(
      of(reviewDto([finding()], { summaryError: 'The model timed out.' })),
    );
    expect(q('summary-error')!.textContent).toContain('The model timed out.');
    api.summarise.mockReturnValue(throwError(() => conflict('No findings.')));
    q('findings-summarise')!.click();
    await settle();
    expect(q('summary-nothing')).not.toBeNull();
    expect((q('findings-summarise') as HTMLButtonElement).disabled).toBe(false);
  });
});
