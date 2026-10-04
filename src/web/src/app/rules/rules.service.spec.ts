import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FilterRequest } from './rules.models';
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

  it('lists, syncs and pages the proposals', () => {
    service.list(true).subscribe();
    expect(
      http.expectOne((r) => r.url === '/api/rules/filters').request.params.toString(),
    ).toBe('includeDeleted=true');
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
    expect(filterRequestProblem(filterRequest({ ...edit, to: 'me@example.com', skipInbox: true }))).toBeNull();
  });
});
