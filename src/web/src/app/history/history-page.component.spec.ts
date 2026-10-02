import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of, throwError } from 'rxjs';
import { isActiveJob, JobDto, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { HistoryPage } from './history-page.component';
import {
  ActionBatchDetailDto,
  ActionBatchDto,
  kindLabel,
  undoConfirmMessage,
} from './history.models';
import { HistoryService } from './history.service';

const batch = (over: Partial<ActionBatchDto> = {}): ActionBatchDto => ({
  id: 'b-1',
  kind: 'apply',
  description: 'Applied 3 suggestions',
  messageCount: 3,
  undoOf: null,
  undoneAt: null,
  jobId: 'job-apply',
  createdAt: '2026-01-01T10:00:00Z',
  canUndo: true,
  ...over,
});

const job = (id: string, status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id,
  type: 'undo',
  queue: 'gmail',
  status,
  progress: { done: 1, total: 4, message: 'Chunk 1 of 4' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

const paged = (items: ActionBatchDto[], total = items.length, page = 1, pageSize = 25) =>
  ({ items, page, pageSize, total }) satisfies PagedDto<ActionBatchDto>;

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

describe('history models', () => {
  it('names the kinds and words the undo confirmation', () => {
    expect(kindLabel('apply_rest')).toBe('Apply rest');
    expect(kindLabel('auto_archive')).toBe('Auto-archive');
    expect(kindLabel('something_new')).toBe('Something new');
    expect(undoConfirmMessage(batch({ messageCount: 1 }))).toBe(
      'Restores the labels of 1 message; created labels stay.',
    );
  });
});

describe('HistoryPage', () => {
  let jobs: FakeJobs;
  let api: {
    list: ReturnType<typeof vi.fn>;
    get: ReturnType<typeof vi.fn>;
    undo: ReturnType<typeof vi.fn>;
  };

  async function render(first = paged([batch()])) {
    jobs = new FakeJobs();
    api = {
      list: vi.fn(() => of(first)),
      get: vi.fn(),
      undo: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: JobsService, useValue: jobs },
        { provide: HistoryService, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(HistoryPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    const overlay = (id: string) =>
      document.querySelector<HTMLElement>(`.cdk-overlay-container [data-testid="${id}"]`);
    return { fixture, component: fixture.componentInstance, el, q, all, overlay };
  }

  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('renders the batches and pages on the server', async () => {
    const { fixture, component, all, q } = await render(
      paged(
        [
          batch(),
          batch({ id: 'b-2', kind: 'undo', canUndo: false, undoOf: 'b-0', jobId: null }),
          batch({ id: 'b-0', undoneAt: '2026-01-01T11:00:00Z', canUndo: false, jobId: null }),
        ],
        60,
      ),
    );
    expect(api.list).toHaveBeenCalledWith(1, 25);
    expect(all('batch-row')).toHaveLength(3);
    expect(all('cell-kind').map((c) => c.textContent?.trim())).toEqual(['Apply', 'Undo', 'Apply']);
    expect(all('undo')).toHaveLength(1);
    expect(q('undone-at')?.textContent).toContain('Undone');

    component.onPage({ pageIndex: 1, pageSize: 25, previousPageIndex: 0, length: 60 });
    await fixture.whenStable();
    expect(api.list).toHaveBeenLastCalledWith(2, 25);

    component.onPage({ pageIndex: 1, pageSize: 50, previousPageIndex: 1, length: 60 });
    expect(api.list).toHaveBeenLastCalledWith(1, 50);
  });

  it('shows an empty state', async () => {
    const { q } = await render(paged([]));
    expect(q('no-batches')).not.toBeNull();
  });

  it('opens the detail drawer with names, notes, created labels and the truncated hint', async () => {
    const { fixture, q, overlay } = await render();
    const detail: ActionBatchDetailDto = {
      batch: batch({ messageCount: 900 }),
      rows: [
        {
          id: 'r-1',
          messageId: 'm-1',
          subject: 'Weekly digest',
          labelsAdded: ['Label_1'],
          labelsRemoved: ['INBOX'],
          labelNamesAdded: ['Topic/News'],
          labelNamesRemoved: ['INBOX'],
          note: 'message gone',
          undoneByBatchId: null,
        },
      ],
      truncated: true,
      createdLabels: [{ id: 'Label_1', name: 'Topic/News', type: 'user' }],
    };
    api.get.mockReturnValue(of(detail));

    q('open-detail')?.click();
    await fixture.whenStable();

    expect(api.get).toHaveBeenCalledWith('b-1');
    expect(overlay('row-added')?.textContent).toContain('Topic/News');
    expect(overlay('row-removed')?.textContent).toContain('INBOX');
    expect(overlay('row-note')?.textContent).toContain('message gone');
    expect(overlay('created-labels')?.textContent).toContain('Undo keeps these labels');
    expect(overlay('detail-truncated')?.textContent).toMatch(/first 1 of\s+900/);
  });

  it('confirms the undo, follows its job and refreshes when it finishes', async () => {
    const { fixture, q, overlay } = await render();
    api.undo.mockReturnValue(
      of(batch({ id: 'u-1', kind: 'undo', jobId: 'job-undo', canUndo: false })),
    );

    q('undo')?.click();
    await fixture.whenStable();
    expect(overlay('confirm-ok')?.closest('mat-dialog-container')?.textContent).toContain(
      'Restores the labels of 3 messages; created labels stay.',
    );
    overlay('confirm-ok')?.click();
    await fixture.whenStable();

    expect(api.undo).toHaveBeenCalledWith('b-1');
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(q('row-progress')?.textContent).toContain('Undoing…');

    jobs.held.set([job('job-undo', 'running')]);
    await fixture.whenStable();
    expect(q('row-progress')?.textContent).toContain('Chunk 1 of 4');
    expect(q('undo')).toBeNull();

    api.list.mockReturnValue(
      of(paged([batch({ undoneAt: '2026-01-01T12:00:00Z', canUndo: false })])),
    );
    jobs.held.set([job('job-undo', 'completed', { version: 2 })]);
    await fixture.whenStable();
    expect(api.list).toHaveBeenCalledTimes(3);
    expect(q('row-progress')).toBeNull();
    expect(q('undone-at')).not.toBeNull();
  });

  it('does nothing when the undo is cancelled', async () => {
    const { fixture, q, overlay } = await render();
    q('undo')?.click();
    await fixture.whenStable();
    overlay('confirm-cancel')?.click();
    await fixture.whenStable();
    expect(api.undo).not.toHaveBeenCalled();
  });

  it('re-enables Undo after a 409 (the interceptor shows the reason)', async () => {
    const { fixture, q, overlay } = await render();
    api.undo.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { title: 'Cannot undo', detail: 'The batch is still being applied.' },
          }),
      ),
    );
    q('undo')?.click();
    await fixture.whenStable();
    overlay('confirm-ok')?.click();
    await fixture.whenStable();

    expect(api.undo).toHaveBeenCalledOnce();
    expect(api.list).toHaveBeenCalledOnce();
    expect((q('undo') as HTMLButtonElement).disabled).toBe(false);
  });

  it("shows the progress of a batch's own running job and reloads after a reconnect", async () => {
    const { fixture, q } = await render();
    jobs.held.set([
      job('job-apply', 'running', { progress: { done: 2, total: null, message: null } }),
    ]);
    await fixture.whenStable();
    expect(q('row-progress')?.textContent).toContain('Applying…');

    jobs.reconnects.set(1);
    await fixture.whenStable();
    expect(api.list).toHaveBeenCalledTimes(2);
  });
});
