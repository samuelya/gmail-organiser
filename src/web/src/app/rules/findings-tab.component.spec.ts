import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { Observable, of, Subject, throwError } from 'rxjs';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { FindingsTab } from './findings-tab.component';
import { ruleReview } from './rules-claude.testing';
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

  let claudeChanges: Subject<ExternalReviewDto>;
  let claude: Record<string, ReturnType<typeof vi.fn>>;
  let loaded: ExternalReviewDto[];

  beforeEach(() => (loaded = []));

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
    claudeChanges = new Subject();
    claude = {
      list: vi.fn(() => of({ items: loaded, page: 1, pageSize: 100, total: loaded.length })),
      createReviews: vi.fn(() => of({ created: 1, skipped: 0, items: [ruleReview()] })),
      accept: vi.fn((id: string) =>
        of(
          ruleReview({
            id,
            findingId: 'k1',
            status: 'reviewed',
            verdict: 'agree',
            resolution: 'accepted_claude',
          }),
        ),
      ),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: RulesService, useValue: api },
        { provide: ClaudeService, useValue: claude },
        {
          provide: SettingsService,
          useValue: { getSettings: () => of({ claudeReviewerMode: 'headless_claude_code' }) },
        },
        {
          provide: JobsService,
          useValue: { externalReviewChanges: claudeChanges, reconnects: signal(0) },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        provideRouter([]),
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

  it('renders the policy findings with their fixes and a link to the policy', async () => {
    const policyFix = (kind: 'relabel' | 'delete') => ({
      kind,
      deleteFilterIds: ['f2'],
      create:
        kind === 'relabel'
          ? {
              criteria: filterDto('n', 'b@example.com').criteria,
              action: filterDto('n', 'x').action,
            }
          : null,
    });
    const { all, card } = await render(
      of(
        reviewDto([
          finding({
            id: 'p1',
            kind: 'overlaps_policy',
            policyId: 'pol-1',
            description: 'Covered by the policy filter.',
            fix: policyFix('delete'),
          }),
          finding({
            id: 'p2',
            kind: 'policy_conflict',
            policyId: 'pol-1',
            description: 'Labels against the policy.',
            fix: policyFix('relabel'),
          }),
        ]),
      ),
    );
    expect(all('finding-group').map((g) => g.querySelector('h3')!.textContent!.trim())).toEqual([
      'Covered by a policy filter (1)',
      'Conflicts with a policy (1)',
    ]);
    const conflict = card('Labels against the policy.');
    expect(all('finding-fix', conflict)[0].textContent).toContain(
      "Replace with the policy's filter",
    );
    expect(all('fix-create', conflict)[0].textContent).toContain('from:b@example.com');
    expect(all('fix-delete', conflict)[0].textContent).toContain('from:a@example.com');
    expect(all('finding-policy', conflict)[0].getAttribute('href')).toBe('/policies/pol-1');
    const covered = card('Covered by the policy filter.');
    expect(all('finding-apply', covered)).toHaveLength(1);
    expect(all('finding-policy', card('Covered by the policy filter.'))).toHaveLength(1);
  });

  const applied = () => finding({ status: 'applied', appliedAt: new Date().toISOString() });

  it('applies a fix after confirming and updates that finding in place', async () => {
    const { q, all, card, settle } = await render();
    api.latestReview.mockReturnValue(
      of({ ...standard(), findings: [applied(), ...standard().findings.slice(1)] }),
    );
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

  it('reloads the review after a fix so other findings show the filters it deleted', async () => {
    const { all, card, settle } = await render();
    const gone = { ...filterDto('f2', 'a@example.com'), deletedAt: new Date().toISOString() };
    const [, k2, ...rest] = standard().findings;
    api.latestReview.mockReturnValue(
      of({
        ...standard(),
        findings: [applied(), { ...k2, filters: [k2.filters[0], gone] }, ...rest],
      }),
    );
    expect(card('Mergeable senders.').textContent).not.toContain('(deleted)');
    all('finding-apply', card('Two filters do the same thing.'))[0].click();
    await settle();
    dialog('confirm-ok')!.click();
    await settle();
    await settle();
    expect(api.latestReview).toHaveBeenCalledTimes(2);
    expect(card('Mergeable senders.').textContent).toContain('(deleted)');
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

  it('sends an open finding to Claude, not a resolved one, and shows its criteria live', async () => {
    const { all, card, settle } = await render();
    expect(all('claude-send', card('Two filters do the same thing.'))).toHaveLength(1);
    expect(all('claude-panel', all('findings-resolved')[0])).toHaveLength(0);
    all('claude-send', card('Two filters do the same thing.'))[0].click();
    await settle();
    expect(claude['createReviews']).toHaveBeenCalledWith({ findingIds: ['k1'] });

    claudeChanges.next(
      ruleReview({
        findingId: 'k2',
        status: 'reviewed',
        verdict: 'alternative',
        verdictFilterCriteria: 'from:(a@example.com OR b@example.com)',
      }),
    );
    await settle();
    const merge = card('Mergeable senders.');
    expect(all('claude-verdict', merge)[0].textContent).toContain('Suggests other criteria');
    expect(all('claude-criteria', merge)[0].textContent).toContain(
      'from:(a@example.com OR b@example.com)',
    );
    expect(all('claude-verdict', card('Two filters do the same thing.'))).toHaveLength(0);
  });

  it('restores a loaded verdict; Accept records it and keeps Apply fix for the user', async () => {
    loaded = [ruleReview({ id: 'x-9', findingId: 'k1', status: 'reviewed', verdict: 'agree' })];
    const { all, card, settle } = await render();
    const dup = card('Two filters do the same thing.');
    expect(claude['list']).toHaveBeenCalledWith(1, 100, {
      findingIds: expect.arrayContaining(['k1']),
    });
    expect(all('claude-verdict', dup)[0].textContent).toContain('Agrees');
    all('claude-accept', dup)[0].click();
    await settle();
    expect(claude['accept']).toHaveBeenCalledWith('x-9');
    expect(api['applyFinding']).not.toHaveBeenCalled();
    expect(all('claude-resolution', dup)[0].textContent).toContain("Accepted Claude's");
    expect(all('finding-apply', dup)).toHaveLength(1);
  });

  it('leaves Accept disabled for needs-human', async () => {
    loaded = [ruleReview({ findingId: 'k1', status: 'reviewed', verdict: 'needs_human' })];
    const { all, card } = await render();
    const dup = card('Two filters do the same thing.');
    expect(all('claude-verdict', dup)[0].textContent).toContain('Needs a human');
    expect((all('claude-accept', dup)[0] as HTMLButtonElement).disabled).toBe(true);
  });
});
