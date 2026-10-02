import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, throwError } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SendersService } from '../senders/senders.service';
import { SettingsService } from '../settings/settings.service';
import { AnalysePage, PREVIEW_DEBOUNCE_MS } from './analyse-page.component';
import {
  ANALYSIS_RUN_JOB,
  AnalysisRunDto,
  GroupingPreviewDto,
  parseAnalyseParams,
  queueOrder,
  savingsText,
} from './analysis.models';
import { AnalysisService } from './analysis.service';

const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: ANALYSIS_RUN_JOB,
  queue: 'analysis',
  status,
  progress: { done: 1, total: 4, message: 'Group 1 of 4' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

const run = (over: Partial<AnalysisRunDto> = {}): AnalysisRunDto => ({
  id: 'run-1',
  jobId: 'job-1',
  scope: 'inbox',
  senderAddress: null,
  requestedCount: 20,
  groupingMode: 'auto',
  status: 'running',
  messagesCovered: 0,
  messagesLlm: 0,
  messagesDerived: 0,
  messagesFromMemory: 0,
  llmCalls: 0,
  groups: 0,
  mixedGroups: 0,
  failedMessages: 0,
  skippedMessages: 0,
  model: null,
  promptVersion: null,
  error: null,
  savedPercent: 0,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  finishedAt: null,
  ...over,
});

const preview = (over: Partial<GroupingPreviewDto> = {}): GroupingPreviewDto => ({
  messages: 20,
  skipped: 0,
  groups: 5,
  estimatedLlmCalls: 5,
  estimatedDerived: 12,
  estimatedFromMemory: 0,
  embeddingsAvailable: true,
  largestGroups: [
    {
      key: 'g1',
      senderAddress: 'news@example.com',
      display: 'Weekly news',
      size: 8,
      representatives: 2,
    },
  ],
  ...over,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('analysis models', () => {
  it('savings read as saved, or as extra calls when retries outnumber the emails', () => {
    expect(savingsText(5, 20, 0.75)).toBe('5 LLM calls for 20 emails (75 % saved)');
    expect(savingsText(6, 4, -0.5)).toBe('6 LLM calls for 4 emails (50 % more calls than emails)');
    expect(savingsText(0, 0, 0)).toBe('0 LLM calls, no emails covered');
  });

  it('the deep link drops an invalid count or sender', () => {
    expect(
      parseAnalyseParams(convertToParamMap({ sender: ' a@example.com ', count: '30' })),
    ).toEqual({ sender: 'a@example.com', count: 30 });
    expect(parseAnalyseParams(convertToParamMap({ count: '0' }))).toEqual({
      sender: null,
      count: null,
    });
    expect(parseAnalyseParams(convertToParamMap({ count: '2.5' })).count).toBeNull();
  });

  it('the queue lists the running run first, then queued runs oldest first', () => {
    const runs = [
      run({ id: 'q-new', status: 'queued', createdAt: '2026-01-01T00:03:00Z' }),
      run({ id: 'q-old', status: 'queued', createdAt: '2026-01-01T00:02:00Z' }),
      run({ id: 'running', status: 'running', createdAt: '2026-01-01T00:01:00Z' }),
    ];
    expect(queueOrder(runs).map((r) => r.id)).toEqual(['running', 'q-old', 'q-new']);
  });
});

describe('AnalysePage', () => {
  let jobs: FakeJobs;
  let api: {
    preview: ReturnType<typeof vi.fn>;
    start: ReturnType<typeof vi.fn>;
    listRuns: ReturnType<typeof vi.fn>;
    cancel: ReturnType<typeof vi.fn>;
  };
  let active: AnalysisRunDto[];
  let finished: AnalysisRunDto[];

  async function render(url = '/analyse', defaultCount = 20) {
    jobs = new FakeJobs();
    active = [];
    finished = [];
    api = {
      preview: vi.fn(() => of(preview())),
      start: vi.fn(() => of(run({ id: 'run-new', status: 'queued' }))),
      listRuns: vi.fn((isActive: boolean) => of(isActive ? active : finished)),
      cancel: vi.fn(() => of(run({ status: 'cancelled' }))),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'analyse', component: AnalysePage }]),
        { provide: JobsService, useValue: jobs },
        { provide: AnalysisService, useValue: api },
        {
          provide: SendersService,
          useValue: { list: vi.fn(() => of({ items: [], page: 1, pageSize: 10, total: 0 })) },
        },
        {
          provide: SettingsService,
          useValue: { getSettings: () => of({ analysisDefaultCount: defaultCount }) },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl(url, AnalysePage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    return { harness, component, el, q, all };
  }

  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('debounces the preview and renders counts, groups and the embeddings notice', async () => {
    const { harness, component, q } = await render();
    api.preview.mockReturnValue(of(preview({ embeddingsAvailable: false })));
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    await harness.fixture.whenStable();
    expect(api.preview).toHaveBeenCalledTimes(1);
    expect(api.preview).toHaveBeenLastCalledWith({ scope: 'inbox', count: 20 });

    component.form.patchValue({ preset: 10 });
    component.form.patchValue({ preset: 50 });
    await wait(PREVIEW_DEBOUNCE_MS / 2);
    expect(api.preview).toHaveBeenCalledTimes(1);
    await wait(PREVIEW_DEBOUNCE_MS);
    await harness.fixture.whenStable();
    expect(api.preview).toHaveBeenCalledTimes(2);
    expect(api.preview).toHaveBeenLastCalledWith({ scope: 'inbox', count: 50 });

    const summary = q('preview-summary')!.textContent!.replace(/\s+/g, ' ');
    expect(summary).toContain(
      '20 emails → 5 groups → ≈ 5 LLM calls, ≈ 12 derived, ≈ 0 from memory',
    );
    expect(q('preview-group')!.textContent).toContain('Weekly news');
    expect(q('no-embeddings')).not.toBeNull();
  });

  it('a default count outside the presets selects Custom', async () => {
    const { component } = await render('/analyse', 35);
    expect(component.form.getRawValue()).toMatchObject({ preset: 'custom', customCount: 35 });
    expect(component.selection()).toEqual({ scope: 'inbox', count: 35 });
  });

  it('the deep link preselects the sender scope and count over the settings default', async () => {
    const { component, q } = await render('/analyse?sender=news@example.com&count=50');
    expect(component.form.getRawValue()).toMatchObject({
      scope: 'sender',
      sender: 'news@example.com',
      preset: 50,
    });
    expect((q('sender') as HTMLInputElement).value).toBe('news@example.com');
    expect(component.selection()).toEqual({
      scope: 'sender',
      senderAddress: 'news@example.com',
      count: 50,
    });
  });

  it('the sender scope needs an address before Start is enabled', async () => {
    const { harness, component, q } = await render();
    component.form.patchValue({ scope: 'sender' });
    await harness.fixture.whenStable();
    expect((q('start') as HTMLButtonElement).disabled).toBe(true);
  });

  it('Start posts the selection and the new run appears in the queue', async () => {
    const { harness, q, all } = await render();
    expect(q('no-active-runs')).not.toBeNull();
    active = [run({ id: 'run-new', status: 'queued' })];
    q('start')!.click();
    await harness.fixture.whenStable();
    expect(api.start).toHaveBeenCalledWith({ scope: 'inbox', count: 20 });
    expect(all('active-run')).toHaveLength(1);
    expect(q('run-status')!.textContent).toContain('Queued');
  });

  it('the preview refreshes after Start and when an active run finishes', async () => {
    const { harness, q } = await render();
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    expect(api.preview).toHaveBeenCalledTimes(1);

    active = [run({ id: 'run-new', status: 'queued' })];
    q('start')!.click();
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    await harness.fixture.whenStable();
    expect(api.preview).toHaveBeenCalledTimes(2);

    active = [];
    jobs.held.set([job('completed')]);
    await harness.fixture.whenStable();
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    expect(api.preview).toHaveBeenCalledTimes(3);
    expect(api.preview).toHaveBeenLastCalledWith({ scope: 'inbox', count: 20 });
  });

  it('a new run joins the end of the queue', async () => {
    const { harness, component } = await render();
    active = [run({ id: 'run-1', status: 'running' })];
    component.loadRuns();
    await harness.fixture.whenStable();
    active = [
      run({ id: 'run-new', status: 'queued', createdAt: '2026-01-02T00:00:00Z' }),
      ...active,
    ];
    component.start();
    await harness.fixture.whenStable();
    expect(component.active().map((r) => r.id)).toEqual(['run-1', 'run-new']);
  });

  it('a 409 on Start links to Settings', async () => {
    const { harness, q } = await render();
    api.start.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 409, statusText: 'Conflict' })),
    );
    q('start')!.click();
    await harness.fixture.whenStable();
    expect(q('no-chat-model')!.querySelector('a')!.getAttribute('href')).toBe('/settings');
  });

  it('hub updates move the progress; a paused job shows paused; a finished job reloads the lists', async () => {
    const { harness, component, q } = await render();
    active = [run()];
    component.loadRuns();
    await harness.fixture.whenStable();
    expect(q('run-progress')!.getAttribute('mode')).toBe('indeterminate');

    jobs.held.set([job('running')]);
    await harness.fixture.whenStable();
    expect(q('run-message')!.textContent).toContain('Group 1 of 4');
    expect(q('run-progress')!.getAttribute('aria-valuenow')).toBe('25');

    jobs.held.set([job('running', { version: 2, progress: { done: 3, total: 4, message: null } })]);
    await harness.fixture.whenStable();
    expect(q('run-progress')!.getAttribute('aria-valuenow')).toBe('75');

    jobs.held.set([job('paused', { version: 3 })]);
    await harness.fixture.whenStable();
    expect(q('run-status')!.textContent).toContain('Paused');

    const calls = api.listRuns.mock.calls.length;
    active = [];
    finished = [
      run({ status: 'completed', llmCalls: 5, messagesCovered: 20, savedPercent: 0.75 }),
      run({ id: 'run-0', status: 'failed', error: 'Model unreachable', savedPercent: -0.5 }),
    ];
    jobs.held.set([job('completed', { version: 4 })]);
    await harness.fixture.whenStable();
    expect(api.listRuns.mock.calls.length).toBe(calls + 2);
    expect(q('no-active-runs')).not.toBeNull();
    expect(q('run-savings')!.textContent).toContain('5 LLM calls for 20 emails (75 % saved)');
    expect(q('run-error')!.textContent).toContain('Model unreachable');
  });

  it('Cancel asks first, then calls the run cancel endpoint', async () => {
    const { harness, component, q } = await render();
    active = [run()];
    component.loadRuns();
    await harness.fixture.whenStable();

    q('cancel-run')!.click();
    await harness.fixture.whenStable();
    [...document.querySelectorAll<HTMLButtonElement>('mat-dialog-container button')]
      .find((b) => b.textContent?.includes('Cancel run'))!
      .click();
    await harness.fixture.whenStable();
    expect(api.cancel).toHaveBeenCalledWith('run-1');
    expect(q('cancel-run')!.textContent).toContain('Cancelling…');
  });
});
