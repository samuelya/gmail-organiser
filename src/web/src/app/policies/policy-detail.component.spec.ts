import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, Subject, throwError } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { LabelsService } from '../review/labels.service';
import { SettingsService } from '../settings/settings.service';
import { POLICY_APPLY_JOB, PolicyRuleDto, SenderPolicyDetailDto } from './policies.models';
import { PoliciesService } from './policies.service';
import { PolicyDetail } from './policy-detail.component';

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  readonly fetch = vi.fn();
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

const rule = (over: Partial<PolicyRuleDto> = {}): PolicyRuleDto => ({
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
  mailType: null,
  retentionDays: null,
  action: 'keep',
  status: 'proposed',
  source: 'llm',
  reason: '',
  matchCount: 4,
  sampleSubjects: ['Your receipt'],
  ...over,
});

const detail = (over: Partial<SenderPolicyDetailDto['policy']> = {}): SenderPolicyDetailDto => ({
  policy: {
    id: 'p1',
    scope: 'sender',
    scopeKey: 'shop@example.com',
    displayName: 'Example Shop',
    isMixed: true,
    topicLabel: null,
    documentTypeLabel: null,
    mailType: null,
    retentionDays: null,
    action: 'keep',
    confidence: 0.8,
    reason: 'Receipts and offers.',
    model: null,
    promptVersion: null,
    status: 'proposed',
    edited: false,
    createdAt: '2026-01-01T00:00:00Z',
    decidedAt: null,
    appliedAt: null,
    ruleCount: 2,
    messageCount: 20,
    kind: 'bulk',
    unreadRatio: 0.5,
    ...over,
  },
  rules: [rule(), rule({ id: 'r2', position: 1, name: 'Offers', matchCount: 6 })],
  defaultCount: 0,
  unmatchedCount: 10,
  guardedCount: 0,
  sampledMessages: 20,
  sampled: false,
  profileTemplates: [],
});

describe('PolicyDetail', () => {
  let api: Record<
    'get' | 'edit' | 'approve' | 'apply' | 'reject' | 'decideRule',
    ReturnType<typeof vi.fn>
  >;
  let jobs: FakeJobs;
  let snack: { open: ReturnType<typeof vi.fn> };

  async function render() {
    api = {
      get: vi.fn(() => of(detail())),
      edit: vi.fn(),
      approve: vi.fn(),
      apply: vi.fn(),
      reject: vi.fn(),
      decideRule: vi.fn(),
    };
    snack = { open: vi.fn(() => ({ onAction: () => new Subject<void>() })) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'policies/:id', component: PolicyDetail }]),
        { provide: JobsService, useValue: (jobs = new FakeJobs()) },
        { provide: PoliciesService, useValue: api },
        { provide: LabelsService, useValue: { labels: () => of([]) } },
        {
          provide: SettingsService,
          useValue: { getSettings: () => of({ documentTypeParent: null }) },
        },
        { provide: MatSnackBar, useValue: snack },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/policies/p1', PolicyDetail);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
    const all = (id: string) => [...el.querySelectorAll(`[data-testid="${id}"]`)] as HTMLElement[];
    const stable = () => harness.fixture.whenStable();
    return { harness, el, q, all, stable };
  }

  async function approveViaDialog(
    stable: () => Promise<void>,
    q: (id: string) => HTMLElement | null,
  ) {
    q('approve')!.click();
    await stable();
    const text = document.querySelector('[data-testid="approve-matched"]')!.textContent!;
    (document.querySelector('[data-testid="approve-ok"]') as HTMLButtonElement).click();
    await stable();
    return text;
  }

  it('confirms approve with the matched count and the mixed note, then follows the apply job', async () => {
    const { q, stable } = await render();
    const queued = { id: 'job-1', type: POLICY_APPLY_JOB, status: 'queued', version: 1 } as JobDto;
    api.approve.mockReturnValue(of({ policy: detail().policy, jobId: 'job-1' }));

    q('approve')!.click();
    await stable();
    expect(document.querySelector('[data-testid="approve-matched"]')!.textContent).toContain(
      '10 of 20',
    );
    expect(document.querySelector('[data-testid="approve-mixed"]')!.textContent).toContain(
      'unmatched mail stays for analysis',
    );
    (document.querySelector('[data-testid="approve-ok"]') as HTMLButtonElement).click();
    await stable();

    expect(api.approve).toHaveBeenCalledWith('p1');
    expect(q('job-progress')).not.toBeNull();

    jobs.held.set([
      {
        ...queued,
        status: 'running',
        version: 2,
        progress: { done: 5, total: 10, message: 'Applying' },
      },
    ]);
    await stable();
    expect(q('job-message')!.textContent).toContain('Applying');

    const loads = api.get.mock.calls.length;
    jobs.held.set([
      {
        ...queued,
        status: 'completed',
        version: 3,
        progress: { done: 10, total: 10, message: 'Applied to 10 messages.' },
      },
    ]);
    await stable();
    expect(q('job-progress')).toBeNull();
    expect(snack.open).toHaveBeenCalledWith(
      'Applied to 10 messages.',
      'History',
      expect.anything(),
    );
    expect(api.get.mock.calls.length).toBeGreaterThan(loads);
  });

  it('on reconnect, ends an apply job that finished while the hub was down', async () => {
    const { q, stable } = await render();
    api.approve.mockReturnValue(of({ policy: detail().policy, jobId: 'job-1' }));
    await approveViaDialog(stable, q);
    expect(q('job-progress')).not.toBeNull();

    jobs.fetch.mockReturnValue(
      of({ id: 'job-1', type: POLICY_APPLY_JOB, status: 'completed', version: 4, progress: null }),
    );
    jobs.reconnects.set(1);
    await stable();

    expect(jobs.fetch).toHaveBeenCalledWith('job-1');
    expect(q('job-progress')).toBeNull();
  });

  it('shows the 422 reason on the page', async () => {
    const { q, stable } = await render();
    api.approve.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 422,
            error: {
              title: 'Policy cannot be applied',
              detail: 'Rule "Offers" has no topic label.',
            },
          }),
      ),
    );
    await approveViaDialog(stable, q);
    expect(q('action-error')!.textContent).toContain('has no topic label');
    expect(q('job-progress')).toBeNull();
  });

  it('saves a reordered rule list and offers Apply again when the API asks', async () => {
    const { q, all, stable } = await render();
    api.get.mockReturnValue(of(detail({ status: 'approved' })));
    api.edit.mockReturnValue(of({ policy: detail({ status: 'approved' }), reapply: true }));
    // Reloaded as approved.
    jobs.reconnects.set(1);
    await stable();
    expect(q('approve')).toBeNull();

    all('rule-down')[0].click();
    await stable();
    expect(all('rule-name').map((c) => c.textContent!.trim().split('\n')[0])).toEqual([
      'Offers',
      'Receipts',
    ]);
    expect(q('rule-approve')).not.toBeNull();
    expect((q('rule-approve') as HTMLButtonElement).disabled).toBe(true);

    q('save')!.click();
    await stable();
    expect(api.edit).toHaveBeenCalledWith(
      'p1',
      expect.objectContaining({
        rules: [expect.objectContaining({ id: 'r2' }), expect.objectContaining({ id: 'r1' })],
      }),
    );
    expect(q('apply-again')).not.toBeNull();
  });

  it('shows server validation errors on their fields', async () => {
    const { q, all, stable } = await render();
    api.edit.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              errors: {
                action: ['A mixed sender’s default cannot delete.'],
                'rules[1].topicLabel': ['Required.'],
              },
            },
          }),
      ),
    );
    all('rule-down')[0].click();
    await stable();
    q('save')!.click();
    await stable();

    expect(q('policy-action-error')!.textContent).toContain('cannot delete');
    expect(q('rule-label-error')!.textContent).toContain('Required.');
  });

  it('shows an error on a rule id at form level, keeps Save usable and offers a reload', async () => {
    const { q, all, stable } = await render();
    api.edit.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { errors: { 'rules[0].id': ['Not a rule of this policy, or listed twice.'] } },
          }),
      ),
    );
    all('rule-down')[0].click();
    await stable();
    q('save')!.click();
    await stable();

    expect(q('form-error')!.textContent).toContain('Rule 1: Not a rule of this policy');
    expect(q('form-error')!.textContent).toContain('reload');
    q('save')!.click();
    await stable();
    expect(api.edit).toHaveBeenCalledTimes(2);

    api.get.mockClear();
    q('reload')!.click();
    await stable();
    expect(api.get).toHaveBeenCalledWith('p1');
    expect(q('form-error')).toBeNull();
    expect(q('reload')).toBeNull();
  });
});
