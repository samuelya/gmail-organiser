import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  criteriaText,
  FilterDto,
  FilterFindingDto,
  FilterRequest,
  fixView,
  groupFindings,
} from './rules.models';
import { filterEdit, filterRequest, filterRequestProblem, RulesService } from './rules.service';

const suggested: FilterRequest = {
  criteria: {
    from: 'news@example.com',
    to: null,
    subject: null,
    query: null,
    negatedQuery: 'has:attachment',
    hasAttachment: null,
    excludeChats: null,
    size: null,
    sizeComparison: null,
  },
  action: { addLabelNames: ['Topic/Alpha', 'Archive/Weekly'], skipInbox: true, markRead: false },
};

describe('RulesService', () => {
  let service: RulesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RulesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps a 404 latest review to null and posts review, apply, dismiss and summary', () => {
    let latest: unknown = 'unset';
    service.latestReview().subscribe((r) => (latest = r));
    const req = http.expectOne('/api/rules/filters/reviews/latest');
    req.flush(null, { status: 404, statusText: 'Not Found' });
    expect(latest).toBeNull();

    service.startReview().subscribe();
    const start = http.expectOne('/api/rules/filters/reviews');
    expect(start.request.method).toBe('POST');
    start.flush({});
    service.applyFinding('a/b').subscribe();
    http.expectOne('/api/rules/filters/findings/a%2Fb/apply').flush({});
    service.dismissFinding('d1').subscribe();
    http.expectOne('/api/rules/filters/findings/d1/dismiss').flush({});
    service.summarise('r1').subscribe();
    http.expectOne('/api/rules/filters/reviews/r1/summary').flush({});
  });

  it('rethrows other latest-review errors', () => {
    const errors: unknown[] = [];
    service.latestReview().subscribe({ error: (e) => errors.push(e) });
    http
      .expectOne('/api/rules/filters/reviews/latest')
      .flush(null, { status: 500, statusText: 'Server Error' });
    expect(errors).toHaveLength(1);
  });

  it('lists, syncs and pages the proposals', () => {
    service.list(true).subscribe();
    expect(http.expectOne((r) => r.url === '/api/rules/filters').request.params.toString()).toBe(
      'includeDeleted=true',
    );
    service.sync().subscribe();
    expect(http.expectOne('/api/rules/filters/sync').request.method).toBe('POST');
    service.proposals(2).subscribe();
    expect(
      http.expectOne((r) => r.url === '/api/rules/filters/proposals').request.params.toString(),
    ).toBe('page=2&pageSize=20');
  });

  it('encodes the id on delete and restore', () => {
    service.delete('a/b').subscribe();
    expect(http.expectOne('/api/rules/filters/a%2Fb').request.method).toBe('DELETE');
    service.restore('a/b').subscribe();
    expect(http.expectOne('/api/rules/filters/a%2Fb/restore').request.method).toBe('POST');
  });

  it('posts the preview and create requests', () => {
    service.preview(suggested).subscribe();
    expect(http.expectOne('/api/rules/filters/preview').request.body).toEqual(suggested);
    service.create(suggested).subscribe();
    const create = http.expectOne('/api/rules/filters');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual(suggested);
  });
});

describe('filter request mapping', () => {
  it('fills the form from a suggestion, the archive label only when it is a rule label', () => {
    const edit = filterEdit(suggested, 'ignored@example.com', ['Archive/Weekly']);
    expect(edit).toEqual({
      from: 'news@example.com',
      to: '',
      subject: '',
      hasAttachment: false,
      query: '',
      negatedQuery: 'has:attachment',
      labelPath: 'Topic/Alpha',
      archiveLabel: 'Archive/Weekly',
      skipInbox: true,
      markRead: false,
    });
    expect(filterEdit(suggested, '', []).archiveLabel).toBe('');
  });

  it('fills only from without a suggestion', () => {
    const edit = filterEdit(null, 'shop@example.com', []);
    expect(edit.from).toBe('shop@example.com');
    expect(edit.labelPath).toBe('');
    expect(filterRequestProblem(filterRequest(edit))).toContain('at least one action');
  });

  it('maps the form back: blanks unset, labels trimmed and de-duplicated', () => {
    const edit = filterEdit(suggested, '', ['Archive/Weekly']);
    const request = filterRequest({
      ...edit,
      subject: '  ',
      hasAttachment: true,
      labelPath: ' Archive/Weekly ',
      markRead: true,
    });
    expect(request.criteria.subject).toBeNull();
    expect(request.criteria.hasAttachment).toBe(true);
    expect(request.action).toEqual({
      addLabelNames: ['Archive/Weekly'],
      skipInbox: true,
      markRead: true,
    });
    expect(filterRequest(edit)).toEqual(suggested);
  });

  it('needs a criterion and an action', () => {
    const edit = filterEdit(null, '', []);
    expect(filterRequestProblem(filterRequest({ ...edit, skipInbox: true }))).toContain(
      'criterion',
    );
    expect(
      filterRequestProblem(filterRequest({ ...edit, to: 'me@example.com', skipInbox: true })),
    ).toBeNull();
  });
});

const row = (id: string, summary: string): FilterDto => ({
  id,
  criteria: { ...suggested.criteria, negatedQuery: null },
  criteriaSummary: summary,
  action: { addLabels: [], removeLabelIds: [], skipInbox: false, markRead: false, forwards: false },
  createdByApp: false,
  firstSeenAt: '2026-01-01T00:00:00Z',
  deletedAt: null,
  deletedByApp: false,
  restoredFrom: null,
});

const finding = (over: Partial<FilterFindingDto> = {}): FilterFindingDto => ({
  id: 'x',
  kind: 'duplicate',
  filterIds: ['f1', 'f2'],
  filters: [row('f1', 'from:a@example.com'), row('f2', 'from:b@example.com')],
  description: 'Synthetic finding.',
  fix: { kind: 'delete', deleteFilterIds: ['f2'], create: null },
  status: 'open',
  appliedAt: null,
  error: null,
  reviewId: 'r1',
  ...over,
});

describe('findings helpers', () => {
  it('groups open findings by kind in display order and collects the resolved ones', () => {
    const { groups, resolved } = groupFindings([
      finding({ id: '1', kind: 'mergeable' }),
      finding({ id: '2', kind: 'duplicate' }),
      finding({ id: '3', kind: 'mergeable' }),
      finding({ id: '4', kind: 'overlap', status: 'applied' }),
      finding({ id: '5', kind: 'duplicate', status: 'superseded' }),
    ]);
    expect(groups.map((g) => [g.kind, g.findings.map((f) => f.id)])).toEqual([
      ['duplicate', ['2']],
      ['mergeable', ['1', '3']],
    ]);
    expect(resolved.map((f) => f.id)).toEqual(['4', '5']);
  });

  it('renders criteria like the API summary', () => {
    expect(
      criteriaText({
        ...suggested.criteria,
        to: 'me@example.com',
        subject: 'Weekly',
        hasAttachment: true,
        size: 1000,
        sizeComparison: 'larger',
        query: 'list:x',
      }),
    ).toBe(
      'from:news@example.com to:me@example.com subject:"Weekly" has:attachment larger:1000 -(has:attachment) (list:x)',
    );
  });

  it('renders a delete fix with the deleted filter, a merge fix with the result, none as null', () => {
    expect(fixView(finding())).toEqual({
      title: 'Delete the redundant filter',
      create: null,
      deletes: ['from:b@example.com'],
    });
    const merge = fixView(
      finding({
        kind: 'mergeable',
        fix: {
          kind: 'merge',
          deleteFilterIds: ['f1', 'gone'],
          create: {
            criteria: {
              ...suggested.criteria,
              from: 'a@example.com OR b@example.com',
              negatedQuery: null,
            },
            action: {
              addLabels: [{ id: 'L1', name: 'Topic/Alpha' }],
              removeLabelIds: ['INBOX'],
              skipInbox: true,
              markRead: false,
              forwards: false,
            },
          },
        },
      }),
    );
    expect(merge).toEqual({
      title: 'Merge into one filter',
      create: {
        criteria: 'from:a@example.com OR b@example.com',
        chips: ['Topic/Alpha', 'skip inbox'],
      },
      deletes: ['from:a@example.com', 'gone'],
    });
    expect(
      fixView(finding({ fix: { kind: 'none', deleteFilterIds: [], create: null } })),
    ).toBeNull();
  });
});
