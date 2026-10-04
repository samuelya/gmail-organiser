import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { isActiveJob, JobDto, JobStatus } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SendersService } from '../senders/senders.service';
import { SettingsService } from '../settings/settings.service';
import { CleanUpPage } from './clean-up-page.component';
import { CleanupBatch, CleanupMessage, CleanupSender, CleanupSummary } from './clean-up.models';
import { CleanUpService } from './clean-up.service';

const sender = (over: Partial<CleanupSender> = {}): CleanupSender => ({
  address: 'news@example.com',
  displayName: 'Example News',
  count: 3,
  protectedCount: 1,
  oldestAt: '2026-01-01T00:00:00Z',
  newestAt: '2026-02-01T00:00:00Z',
  allowlisted: false,
  allowlistedByDomain: false,
  ...over,
});

const message = (id: string, over: Partial<CleanupMessage> = {}): CleanupMessage => ({
  id,
  subject: `Subject ${id}`,
  snippet: 'A synthetic <b>preview</b>',
  internalDate: '2026-02-01T00:00:00Z',
  sizeEstimate: 2048,
  inInbox: false,
  protectedReason: null,
  ...over,
});

const batch = (jobId = 'job-1'): CleanupBatch => ({
  batch: {
    id: 'batch-1',
    kind: 'trash',
    description: 'Trash',
    messageCount: 2,
    jobId,
    createdAt: '2026-02-01T00:00:00Z',
  },
  queued: 2,
  skippedProtected: 1,
});

const job = (status: JobStatus, over: Partial<JobDto> = {}): JobDto => ({
  id: 'job-1',
  type: 'cleanup_actions',
  queue: 'apply',
  status,
  progress: { done: 1, total: 2, message: 'Trashed 1 of 2 messages' },
  error: null,
  createdAt: '2026-01-01T00:00:00Z',
  startedAt: null,
  updatedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  version: 1,
  ...over,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly reconnects = signal(0);
  cancel = vi.fn(() => of(undefined));
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

describe('CleanUpPage', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(
    summary: CleanupSummary = { messages: 5, senders: 2, protected: 1 },
    senderItems: CleanupSender[] = [
      sender(),
      sender({ address: 'shop@example.com', displayName: null, allowlisted: true }),
    ],
  ) {
    const jobs = new FakeJobs();
    const api = {
      summary: vi.fn(() => of(summary)),
      senders: vi.fn(() =>
        of({
          items: senderItems,
          page: 1,
          pageSize: 25,
          total: senderItems.length,
        }),
      ),
      messages: vi.fn(() =>
        of({
          items: [
            message('a', { inInbox: true }),
            message('b', { protectedReason: 'Has an attachment' }),
          ],
          page: 1,
          pageSize: 50,
          total: 2,
        }),
      ),
      unmark: vi.fn(() => of(batch() as CleanupBatch | null)),
      delete: vi.fn(() => of(batch() as CleanupBatch | null)),
      job: vi.fn(() => of(job('completed', { version: 5 }))),
      unsubscribeInfo: vi.fn(() =>
        of({
          method: null,
          url: null,
          messageId: null,
          unsubscribedAt: null,
          unsubscribedVia: null,
        }),
      ),
    };
    const senders = { setAllowlisted: vi.fn(() => of(sender({ allowlisted: true }))) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: JobsService, useValue: jobs },
        { provide: CleanUpService, useValue: api },
        { provide: SendersService, useValue: senders },
        {
          provide: SettingsService,
          useValue: { getSettings: () => of({ deleteLabelName: 'Bin' }) },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(CleanUpPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, jobs, api, senders, el, q, all, settle };
  }

  const dialog = (testId: string) =>
    document.querySelector<HTMLElement>(`mat-dialog-container [data-testid="${testId}"]`)!;
  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';
  const snackAction = () =>
    document.querySelector<HTMLButtonElement>('mat-snack-bar-container button')!;
  const settleOverlay = () => new Promise((r) => setTimeout(r));

  it('renders the summary, senders with badges and the first sender’s messages', async () => {
    const { api, q, all } = await render();
    expect(q('summary-messages')!.textContent).toContain('5');
    expect(q('summary')!.textContent).toContain('Bin');
    expect(all('sender-item')).toHaveLength(2);
    expect(all('sender-count')[0].textContent).toContain('3');
    expect(all('sender-protected')[0].textContent).toContain('1 protected');
    expect(all('sender-allowlisted')).toHaveLength(1);
    expect(api.messages).toHaveBeenCalledWith('news@example.com', 1, 50);
    expect(q('detail-title')!.textContent).toContain('Example News');
    expect(all('message-row')).toHaveLength(2);
    expect(all('message-inbox')).toHaveLength(1);
    expect(q('message-protected')!.getAttribute('aria-label')).toBe('Protected: Has an attachment');
    // Snippets are plain text, never HTML.
    expect(q('message-snippet')!.textContent).toBe('A synthetic <b>preview</b>');
    expect(q('message-snippet')!.querySelector('b')).toBeNull();
  });

  it('shows the empty state with a link to Review', async () => {
    const { q } = await render({ messages: 0, senders: 0, protected: 0 });
    expect(q('empty')!.textContent).toContain('Nothing is labelled “Bin”');
    expect(q('link-review')!.getAttribute('href')).toBe('/review');
    expect(q('detail')).toBeNull();
    expect((q('delete-all') as HTMLButtonElement).disabled).toBe(true);
  });

  it('selects all on the page and removes the selected rows from the list', async () => {
    const { api, q, settle } = await render();
    q('select-page')!.querySelector('input')!.click();
    await settle();
    expect(q('selection-count')!.textContent).toContain('2 selected');
    q('remove-selected')!.click();
    await settle();
    expect(api.unmark).toHaveBeenCalledWith({ kind: 'ids', ids: ['a', 'b'] });
  });

  it('deletes the selection skipping protected by default', async () => {
    const { api, all, q, settle } = await render();
    for (const check of all('message-check')) check.querySelector('input')!.click();
    await settle();
    q('delete-selected')!.click();
    await settle();
    expect(dialog('delete-count').textContent).toContain('1 message from the selection');
    expect(dialog('delete-protected').textContent).toContain('1 protected message will be skipped');
    dialog('delete-ok').click();
    await settle();
    await settleOverlay();
    expect(api.delete).toHaveBeenCalledWith({ kind: 'ids', ids: ['a', 'b'] }, false);
  });

  it('deletes the whole sender and everything from the header', async () => {
    const { api, q, settle } = await render();
    q('delete-sender')!.click();
    await settle();
    expect(dialog('delete-count').textContent).toContain('2 messages from Example News');
    dialog('include-protected').querySelector('input')!.click();
    await settle();
    dialog('delete-ok').click();
    await settle();
    await settleOverlay();
    expect(api.delete).toHaveBeenCalledWith({ kind: 'sender', address: 'news@example.com' }, true);
  });

  it('follows the job, then refreshes and offers the batch in History', async () => {
    const { api, jobs, q, settle } = await render();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    q('delete-all')!.click();
    await settle();
    expect(dialog('delete-count').textContent).toContain('4 messages from every sender');
    dialog('delete-ok').click();
    await settle();
    await settleOverlay();
    expect(api.delete).toHaveBeenCalledWith({ kind: 'all' }, false);
    jobs.held.set([job('running')]);
    await settle();
    expect(q('job-message')!.textContent).toContain('Trashed 1 of 2 messages');
    expect((q('delete-sender') as HTMLButtonElement).disabled).toBe(true);
    const summaries = api.summary.mock.calls.length;
    jobs.held.set([
      job('completed', {
        version: 2,
        progress: { done: 2, total: 2, message: 'Trashed 2 messages' },
      }),
    ]);
    await settle();
    expect(q('job-progress')).toBeNull();
    expect(api.summary.mock.calls.length).toBeGreaterThan(summaries);
    expect(snackText()).toContain('Trashed 2 messages');
    snackAction().click();
    await settleOverlay();
    expect(navigate).toHaveBeenCalledWith(['/history', 'batch-1']);
  });

  it('says so when there is nothing to do (204)', async () => {
    const { api, q, settle } = await render();
    api.unmark.mockReturnValue(of(null));
    q('remove-sender')!.click();
    await settle();
    dialog('confirm-ok').click();
    await settle();
    await settleOverlay();
    expect(api.unmark).toHaveBeenCalledWith({ kind: 'sender', address: 'news@example.com' });
    expect(snackText()).toContain('Nothing to do');
    expect(q('job-progress')).toBeNull();
  });

  it('says “loses” for a single message when removing a sender (#229)', async () => {
    const { q, settle } = await render(undefined, [sender({ count: 1 })]);
    expect(q('detail-title')!.parentElement!.textContent).toContain('1 message ·');
    q('remove-sender')!.click();
    await settle();
    expect(document.querySelector('mat-dialog-container')!.textContent).toContain(
      '1 message from Example News loses the “Bin” label.',
    );
  });

  it('shows the sender’s Unsubscribe in its header', async () => {
    const { api, q } = await render();
    expect(api.unsubscribeInfo).toHaveBeenCalledWith('news@example.com');
    expect(q('unsubscribe')).not.toBeNull();
  });

  it('allowlists the selected sender and refreshes', async () => {
    const { api, senders, q, settle } = await render();
    const calls = api.senders.mock.calls.length;
    q('allowlist-sender')!.click();
    await settle();
    expect(senders.setAllowlisted).toHaveBeenCalledWith('news@example.com', true);
    expect(api.senders.mock.calls.length).toBeGreaterThan(calls);
  });

  it('goes back to the last sender page when a reload past the end comes back empty', async () => {
    const { api, fixture, all, settle } = await render();
    const first = api.senders.getMockImplementation()!;
    api.senders.mockImplementation((...args: unknown[]) =>
      args[0] === 2 ? of({ items: [], page: 2, pageSize: 25, total: 25 }) : first(),
    );
    fixture.componentInstance.onSenderPage(2);
    await settle();
    expect(api.senders).toHaveBeenLastCalledWith(1, 25, '');
    expect(all('sender-item')).toHaveLength(2);
  });

  it('goes back to the last message page when a reload past the end comes back empty', async () => {
    const { api, fixture, all, settle } = await render();
    const first = api.messages.getMockImplementation()!;
    api.messages.mockImplementation((...args: unknown[]) =>
      args[1] === 2 ? of({ items: [], page: 2, pageSize: 50, total: 50 }) : first(),
    );
    fixture.componentInstance.onMessagePage(2);
    await settle();
    expect(api.messages).toHaveBeenLastCalledWith('news@example.com', 1, 50);
    expect(all('message-row')).toHaveLength(2);
  });
});
