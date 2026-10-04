import { computed, Component, inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatTooltip } from '@angular/material/tooltip';
import { By } from '@angular/platform-browser';
import { of, Subject, throwError } from 'rxjs';
import { AnalysisService } from '../analyse/analysis.service';
import { MAX_COMPARE } from '../analyse/compare-run';
import { isActiveJob, JobDto, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { CardReanalyse } from './card-reanalyse';
import { GroupCard } from './group-card.component';
import { ReviewGroupDto, SuggestionDto, SuggestionStatus } from './review.models';
import { ReviewService } from './review.service';

const member = (id: string, status: SuggestionStatus = 'pending'): SuggestionDto => ({
  id,
  messageId: `m-${id}`,
  subject: `Subject ${id}`,
  date: '2026-01-01T00:00:00Z',
  snippet: 'A synthetic preview',
  source: 'llm',
  topicLabel: 'Topic/Alpha',
  isNewLabel: false,
  needsAction: false,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.9,
  reason: 'Synthetic reason',
  status,
  edited: false,
  protected: false,
  replaceLabels: [],
  currentLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
});

const group = (members: SuggestionDto[], over: Partial<ReviewGroupDto> = {}): ReviewGroupDto => ({
  groupKey: 'key-1',
  display: 'Weekly digest',
  size: members.length,
  llmCount: 1,
  derivedCount: 0,
  memoryCount: 0,
  topicLabel: 'Topic/Alpha',
  needsAction: false,
  toBeDeleted: false,
  mixed: false,
  confidenceMin: 0.9,
  confidenceMax: 0.9,
  reason: 'Synthetic reason',
  members,
  truncated: false,
  replaceLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

const runJob = (status: JobStatus): JobDto => ({
  id: 'job-r',
  type: 'analysis_run',
  queue: 'llm',
  status,
  progress: null,
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

/** The Review page's bindings for one card. */
@Component({
  imports: [GroupCard],
  providers: [CardReanalyse],
  template: `
    <app-group-card
      [group]="group()"
      senderAddress="news@example.com"
      [labels]="{ action: 'Act', delete: 'Bin' }"
      [busy]="false"
      [runActive]="reanalyse.blocked()"
      [reanalysing]="reanalyse.ids()"
      [reanalysingCard]="reanalyse.fromCard()"
      (reanalyseGroup)="reanalyse.card($event)"
      (reanalyseMember)="reanalyse.member($event)"
    />
  `,
})
class Host {
  readonly reanalyse = inject(CardReanalyse);
  readonly group = signal(group([]));
}

describe('Card and member Re-analyse', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(g: ReviewGroupDto, start = () => of({ id: 'run-1', jobId: 'job-r' })) {
    const jobs = new FakeJobs();
    const analysis = { startCompareRun: vi.fn(start) };
    const review = { job: vi.fn(() => of(runJob('running'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: JobsService, useValue: jobs },
        { provide: AnalysisService, useValue: analysis },
        { provide: ReviewService, useValue: review },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.group.set(g);
    const ended = vi.fn();
    fixture.componentInstance.reanalyse.finished.subscribe(ended);
    const el = fixture.nativeElement as HTMLElement;
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    const q = (id: string) => all(id)[0] ?? null;
    // Twice: the dialog closes after a change detection pass.
    const settle = async () => {
      for (let i = 0; i < 2; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
    };
    await settle();
    q('group-expand')!.click();
    await settle();
    const tooltip = (id: string) =>
      fixture.debugElement.query(By.css(`[data-testid="${id}"]`)).injector.get(MatTooltip).message;
    return { jobs, analysis, review, ended, q, all, settle, tooltip };
  }

  const dialogText = () => document.querySelector('mat-dialog-container')?.textContent ?? '';
  const dialogButton = (testId: string) =>
    document.querySelector<HTMLButtonElement>(`mat-dialog-container [data-testid="${testId}"]`)!;

  it('shows on every member whatever its status and re-analyses that one email without a confirm', async () => {
    const statuses: SuggestionStatus[] = ['pending', 'approved', 'rejected', 'applied'];
    const { all, analysis, settle, tooltip } = await render(
      group(statuses.map((s, i) => member(`id-${i}`, s))),
    );
    const buttons = all('member-reanalyse');
    expect(buttons).toHaveLength(4);
    expect(buttons[3].getAttribute('aria-label')).toBe('Re-analyse Subject id-3');
    expect(tooltip('member-reanalyse')).toBe('Re-analyse with the current prompt');
    buttons[3].click();
    await settle();
    expect(dialogText()).toBe('');
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['id-3'] });
  });

  it('spins on the originating member until its run ends, then reloads', async () => {
    const { all, q, jobs, ended, settle, tooltip } = await render(
      group([member('a'), member('b', 'applied')]),
    );
    all('member-reanalyse')[1].click();
    await settle();
    expect(all('member-reanalysing')).toHaveLength(1);
    expect(all('member-row')[1].querySelector('[data-testid="member-reanalysing"]')).not.toBeNull();
    expect(q('group-reanalysing')).toBeNull();
    expect(all('member-reanalyse')[0].getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('group-reanalyse')).toBe('An analysis is running');

    jobs.held.set([runJob('running')]);
    await settle();
    expect(ended).not.toHaveBeenCalled();
    jobs.held.set([runJob('completed')]);
    await settle();
    expect(ended).toHaveBeenCalledTimes(1);
    expect(q('member-reanalysing')).toBeNull();
    expect(all('member-reanalyse')[0].getAttribute('aria-disabled')).not.toBe('true');
  });

  it('the card sends every loaded member, any status, after a confirm and spins on the card', async () => {
    const { q, analysis, settle } = await render(
      group([member('a', 'approved'), member('b', 'applied'), member('c', 'rejected')]),
    );
    q('group-reanalyse')!.click();
    await settle();
    expect(dialogText()).toContain('Re-analyse 3 emails?');
    expect(dialogText()).not.toContain('Only the newest');
    dialogButton('confirm-ok').click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['a', 'b', 'c'] });
    expect(q('group-reanalysing')).not.toBeNull();
    expect(q('member-reanalysing')).toBeNull();
  });

  it('a truncated card says only the loaded members are re-analysed; cancel sends nothing', async () => {
    const { q, analysis, settle } = await render(
      group([member('a'), member('b')], { size: 40, truncated: true }),
    );
    q('group-reanalyse')!.click();
    await settle();
    expect(dialogText()).toContain('Only the newest 2 of 40 members shown are re-analysed.');
    dialogButton('confirm-cancel').click();
    await settle();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
    expect(q('group-reanalyse')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('is disabled over the maximum count', async () => {
    const many = Array.from({ length: MAX_COMPARE + 1 }, (_, i) => member(`id-${i}`));
    const { q, tooltip } = await render(group(many));
    expect(q('group-reanalyse')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('group-reanalyse')).toBe(`At most ${MAX_COMPARE} at a time`);
  });

  it('is disabled on card and members while any analysis run is active', async () => {
    const { q, all, jobs, analysis, settle, tooltip } = await render(group([member('a')]));
    jobs.held.set([runJob('queued')]);
    await settle();
    expect(q('group-reanalyse')!.getAttribute('aria-disabled')).toBe('true');
    expect(all('member-reanalyse')[0].getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('member-reanalyse')).toBe('An analysis is running');
    all('member-reanalyse')[0].click();
    await settle();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
  });

  it('a 409 leaves the card as it was (the error interceptor shows the problem detail)', async () => {
    const failed = new Subject<never>();
    const { q, all, ended, settle } = await render(group([member('a')]), () => failed);
    all('member-reanalyse')[0].click();
    await settle();
    failed.error({ status: 409 });
    await settle();
    expect(q('member-reanalysing')).toBeNull();
    expect(all('member-reanalyse')[0].getAttribute('aria-disabled')).not.toBe('true');
    expect(ended).not.toHaveBeenCalled();
  });

  it('on reconnect before the hub reported the run, it keeps spinning while the API says it is active', async () => {
    const { all, q, jobs, review, ended, settle } = await render(group([member('a')]));
    all('member-reanalyse')[0].click();
    await settle();
    jobs.reconnects.set(1);
    await settle();
    expect(review.job).toHaveBeenCalledWith('job-r');
    expect(ended).not.toHaveBeenCalled();
    expect(q('member-reanalysing')).not.toBeNull();

    jobs.held.set([runJob('completed')]);
    await settle();
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it('on reconnect, a run the API reports finished ends', async () => {
    const { all, jobs, review, ended, settle } = await render(group([member('a')]));
    review.job.mockReturnValue(of(runJob('completed')));
    all('member-reanalyse')[0].click();
    await settle();
    jobs.reconnects.set(1);
    await settle();
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it('on reconnect, a run the API reported active ends once the hub snapshot drops it', async () => {
    const { all, jobs, ended, settle } = await render(group([member('a')]));
    all('member-reanalyse')[0].click();
    await settle();
    jobs.reconnects.set(1);
    await settle();
    jobs.held.set([]);
    await settle();
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it('a 400 from the API keeps nothing in flight', async () => {
    const { q, all, settle } = await render(group([member('a')]), () =>
      throwError(() => ({ status: 400 })),
    );
    all('member-reanalyse')[0].click();
    await settle();
    expect(q('member-reanalysing')).toBeNull();
  });
});
