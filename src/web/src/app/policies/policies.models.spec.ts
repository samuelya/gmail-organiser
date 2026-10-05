import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import {
  applyServerErrors,
  STALE_RULES_ERROR,
  approveSummary,
  controlError,
  editRequest,
  matchSummary,
  moveRule,
  parsePolicyQuery,
  policyForm,
  policyQueryParams,
  PolicyRuleDto,
  ruleForm,
  SenderPolicyDetailDto,
  SenderPolicyDto,
} from './policies.models';
import { PoliciesService } from './policies.service';

export const policy = (over: Partial<SenderPolicyDto> = {}): SenderPolicyDto => ({
  id: 'p1',
  scope: 'sender',
  scopeKey: 'news@example.com',
  displayName: 'Example News',
  isMixed: false,
  topicLabel: 'Newsletters',
  documentTypeLabel: null,
  mailType: 'newsletter',
  retentionDays: null,
  action: 'archive',
  confidence: 0.9,
  reason: 'Weekly digest.',
  model: null,
  promptVersion: null,
  status: 'proposed',
  edited: false,
  createdAt: '2026-01-01T00:00:00Z',
  decidedAt: null,
  appliedAt: null,
  ruleCount: 0,
  messageCount: 40,
  kind: 'bulk',
  unreadRatio: 0.8,
  ...over,
});

export const rule = (over: Partial<PolicyRuleDto> = {}): PolicyRuleDto => ({
  id: 'r1',
  position: 0,
  name: 'Receipts',
  match: {
    listIdPresent: null,
    listUnsubscribePresent: null,
    fromAddress: null,
    fromSubdomain: null,
    category: null,
    subjectTemplate: null,
    subjectContains: 'receipt',
  },
  topicLabel: 'Shopping/Receipts',
  documentTypeLabel: null,
  mailType: 'receipt',
  retentionDays: null,
  action: 'keep',
  status: 'proposed',
  source: 'llm',
  reason: '',
  matchCount: 3,
  sampleSubjects: ['Your receipt'],
  ...over,
});

export const detail = (over: Partial<SenderPolicyDetailDto> = {}): SenderPolicyDetailDto => ({
  policy: policy(),
  rules: [],
  defaultCount: 30,
  unmatchedCount: 0,
  guardedCount: 0,
  sampledMessages: 30,
  sampled: false,
  profileTemplates: [],
  ...over,
});

describe('policy query mapping', () => {
  it('reads the tab, search and page, falling back to the defaults', () => {
    expect(
      parsePolicyQuery(
        convertToParamMap({ status: 'rejected', search: ' shop ', page: '3', pageSize: '50' }),
      ),
    ).toEqual({ status: 'rejected', search: 'shop', page: 3, pageSize: 50 });
    expect(
      parsePolicyQuery(convertToParamMap({ status: 'unknown', page: '0', pageSize: '7' })),
    ).toEqual({ status: 'proposed', search: '', page: 1, pageSize: 25 });
  });

  it('leaves defaults out of the URL', () => {
    expect(policyQueryParams(parsePolicyQuery(convertToParamMap({})))).toEqual({
      status: null,
      search: null,
      page: null,
      pageSize: null,
    });
    expect(policyQueryParams({ status: 'approved', search: 'x', page: 2, pageSize: 100 })).toEqual({
      status: 'approved',
      search: 'x',
      page: 2,
      pageSize: 100,
    });
  });
});

describe('PoliciesService', () => {
  let service: PoliciesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(PoliciesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list sends the status tab, paging and the trimmed search', () => {
    service.list({ status: 'approved', search: ' news ', page: 2, pageSize: 25 }).subscribe();
    const req = http.expectOne((r) => r.url === '/api/policies');
    expect(req.request.params.toString()).toBe('status=approved&page=2&pageSize=25&search=news');
    req.flush({ items: [], page: 2, pageSize: 25, total: 0 });
  });

  it('counts reads each tab’s total', () => {
    let counts: unknown;
    service.counts('').subscribe((c) => (counts = c));
    const reqs = http.match((r) => r.url === '/api/policies');
    expect(reqs.map((r) => r.request.params.get('status'))).toEqual([
      'proposed',
      'approved',
      'rejected',
    ]);
    reqs.forEach((r, i) => r.flush({ items: [], page: 1, pageSize: 1, total: [4, 2, 1][i] }));
    expect(counts).toEqual({ proposed: 4, approved: 2, rejected: 1 });
  });
});

describe('policy form', () => {
  it('builds the PUT body with rules in saved order and blanks as null', () => {
    const form = policyForm(
      detail({
        rules: [
          rule({ id: 'b', position: 1, name: 'B' }),
          rule({ id: 'a', position: 0, name: 'A' }),
        ],
      }),
    );
    form.controls.retentionDays.setValue(30);
    const body = editRequest(form);
    expect(body).toMatchObject({
      topicLabel: 'Newsletters',
      documentTypeLabel: null,
      mailType: 'newsletter',
      retentionDays: 30,
      action: 'archive',
      isMixed: false,
    });
    expect(body.rules.map((r) => r.id)).toEqual(['a', 'b']);
    expect(body.rules[0].match).toEqual({
      fromAddress: null,
      fromSubdomain: null,
      subjectTemplate: null,
      subjectContains: 'receipt',
      category: null,
      listIdPresent: null,
      listUnsubscribePresent: null,
    });
  });

  it('validates labels, retention and a new rule', () => {
    const form = policyForm(detail());
    form.controls.topicLabel.setValue('');
    expect(form.controls.topicLabel.valid).toBe(true);
    form.controls.topicLabel.setValue('INBOX');
    expect(form.controls.topicLabel.invalid).toBe(true);
    form.controls.retentionDays.setValue(0);
    form.controls.retentionDays.markAsTouched();
    expect(controlError(form.controls.retentionDays)).toMatch(/positive/);

    const added = ruleForm();
    added.markAllAsTouched();
    expect(controlError(added.controls.name)).toBe('Required.');
    expect(controlError(added.controls.topicLabel)).toBe('Enter a label.');
    expect(controlError(added.controls.match)).toBe('Set at least one match field.');
    added.controls.match.controls.listIdPresent.setValue('yes');
    expect(added.controls.match.valid).toBe(true);
  });

  it('moves rules up and down within bounds', () => {
    const form = policyForm(
      detail({
        rules: [
          rule({ id: 'a', position: 0 }),
          rule({ id: 'b', position: 1 }),
          rule({ id: 'c', position: 2 }),
        ],
      }),
    );
    const ids = () => form.controls.rules.controls.map((r) => r.controls.id.value);
    moveRule(form.controls.rules, 2, -1);
    expect(ids()).toEqual(['a', 'c', 'b']);
    moveRule(form.controls.rules, 0, -1);
    moveRule(form.controls.rules, 2, 1);
    expect(ids()).toEqual(['a', 'c', 'b']);
    expect(form.dirty).toBe(true);
  });

  it('puts server field errors on their controls and returns the rest', () => {
    const form = policyForm(detail({ rules: [rule()] }));
    const rest = applyServerErrors(form, {
      topicLabel: ['Required unless the sender is mixed.'],
      'rules[0].match': ['Set at least one match field.'],
      'rules[0].id': ['Not a rule of this policy.'],
      rules: ['At most 12 rules.'],
      somethingElse: ['Unknown.'],
    });
    expect(controlError(form.controls.topicLabel)).toBe('Required unless the sender is mixed.');
    const r = form.controls.rules.at(0);
    expect(controlError(r.controls.match)).toBe('Set at least one match field.');
    expect(r.controls.id.errors).toBeNull();
    expect(controlError(form.controls.rules)).toBe('At most 12 rules.');
    expect(rest).toEqual([`Rule 1: Not a rule of this policy. ${STALE_RULES_ERROR}`, 'Unknown.']);
    expect(r.controls.id.valid).toBe(true);
  });

  it('summarises a match and what an approve applies to', () => {
    expect(matchSummary(rule().match)).toBe('subject contains "receipt"');
    expect(
      approveSummary(
        detail({
          policy: policy({ isMixed: true }),
          rules: [rule({ matchCount: 5 }), rule({ id: 'r2', matchCount: 0, status: 'rejected' })],
          defaultCount: 0,
          unmatchedCount: 7,
          sampledMessages: 12,
        }),
      ),
    ).toEqual({
      matched: 5,
      unmatched: 7,
      guarded: 0,
      sampledMessages: 12,
      sampled: false,
      mixed: true,
    });
  });
});
