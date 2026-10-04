import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { LabelsService } from '../review/labels.service';
import { SettingsService } from '../settings/settings.service';
import { FiltersTab } from './filters-tab.component';
import { FilterDto, FilterListDto, FilterProposalDto } from './rules.models';
import { RulesService } from './rules.service';

const filterDto = (over: Partial<FilterDto> = {}): FilterDto => ({
  id: 'f-1',
  criteria: {
    from: 'news@example.com',
    to: null,
    subject: null,
    query: null,
    negatedQuery: null,
    hasAttachment: null,
    excludeChats: null,
    size: null,
    sizeComparison: null,
  },
  criteriaSummary: 'from:news@example.com',
  action: {
    addLabels: [{ id: 'L1', name: 'Topic/Alpha' }],
    removeLabelIds: ['INBOX'],
    skipInbox: true,
    markRead: false,
    forwards: true,
  },
  createdByApp: true,
  firstSeenAt: '2026-01-01T00:00:00Z',
  deletedAt: null,
  deletedByApp: false,
  restoredFrom: null,
  ...over,
});

const listOf = (filters: FilterDto[]): FilterListDto => ({
  syncedAt: new Date(Date.now() - 2 * 3600_000).toISOString(),
  activeCount: filters.filter((f) => !f.deletedAt).length,
  limit: 1000,
  filters,
});

const proposal = (address: string, count = 10): FilterProposalDto => ({
  senderAddress: address,
  displayName: 'Synthetic sender',
  messageCount: count,
  listId: null,
  pattern: {
    topicLabel: 'Topic/Alpha',
    needsAction: true,
    toBeDeleted: false,
    approvals: 3,
    agreement: 1,
    remaining: 0,
  },
  suggested: {
    criteria: { ...filterDto().criteria, from: address },
    action: { addLabelNames: ['Topic/Alpha'], skipInbox: false, markRead: false },
  },
});

const paged = (items: FilterProposalDto[], total = items.length, page = 1) =>
  ({ items, page, pageSize: 20, total }) satisfies PagedDto<FilterProposalDto>;

@Component({
  imports: [FiltersTab],
  template: `<app-filters-tab [propose]="propose()" (proposeHandled)="handled = handled + 1" />`,
})
class Host {
  readonly propose = signal<string | undefined>(undefined);
  handled = 0;
}

describe('FiltersTab', () => {
  let api: Record<
    'list' | 'sync' | 'delete' | 'restore' | 'proposals' | 'preview' | 'create',
    ReturnType<typeof vi.fn>
  >;

  async function render(propose?: string, proposals = paged([proposal('news@example.com')])) {
    api = {
      list: vi.fn((includeDeleted: boolean) =>
        of(
          listOf(
            includeDeleted
              ? [filterDto(), filterDto({ id: 'f-2', deletedAt: '2026-01-02T00:00:00Z', deletedByApp: true })]
              : [filterDto()],
          ),
        ),
      ),
      sync: vi.fn(() => of({ total: 1, added: 1, removed: 0, syncedAt: '2026-01-01T00:00:00Z' })),
      delete: vi.fn(() => of(undefined)),
      restore: vi.fn(() => of(filterDto({ id: 'f-3' }))),
      proposals: vi.fn(() => of(proposals)),
      preview: vi.fn(() =>
        of({
          criteria: filterDto().criteria,
          criteriaSummary: '',
          query: 'from:news@example.com',
          localMatches: 3,
          gmailEstimate: 5,
          action: filterDto().action,
          createsLabels: [],
          warnings: [],
        }),
      ),
      create: vi.fn(() => of(filterDto({ id: 'f-new' }))),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: RulesService, useValue: api },
        { provide: LabelsService, useValue: { labels: () => of([]), refresh: () => of([]) } },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () => of({ actionLabelName: 'Act', deleteLabelName: 'Bin' }),
          },
        },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.propose.set(propose);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, el, q, all, settle };
  }

  const dialog = (testId: string) =>
    document.querySelector<HTMLElement>(`mat-dialog-container [data-testid="${testId}"]`);

  afterEach(() => document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = '')));

  it('lists the filters with the status line, action chips and app icon', async () => {
    const { q, all } = await render();
    expect(api.list).toHaveBeenCalledWith(false);
    expect(q('filters-status')!.textContent).toContain('1 of 1,000 filters, synced 2 hours ago');
    expect(all('filter-chip').map((c) => c.textContent!.trim())).toEqual([
      'Topic/Alpha',
      'skip inbox',
      'forwards',
    ]);
    expect(q('filter-by-app')).not.toBeNull();
  });

  it('syncs from Gmail and reloads', async () => {
    const { q, settle } = await render();
    q('filters-sync')!.click();
    await settle();
    expect(api.sync).toHaveBeenCalled();
    expect(api.list).toHaveBeenCalledTimes(2);
  });

  it('deletes a filter only after confirming', async () => {
    const { q, settle } = await render();
    q('filter-delete')!.click();
    await settle();
    dialog('confirm-cancel')!.click();
    await settle();
    expect(api.delete).not.toHaveBeenCalled();
    q('filter-delete')!.click();
    await settle();
    dialog('confirm-ok')!.click();
    await settle();
    expect(api.delete).toHaveBeenCalledWith('f-1');
    expect(api.list).toHaveBeenCalledTimes(2);
  });

  it('shows deleted filters greyed with Restore', async () => {
    const { q, all, settle } = await render();
    q('filters-show-deleted')!.querySelector('button')!.click();
    await settle();
    expect(api.list).toHaveBeenLastCalledWith(true);
    expect(all('filter-row')[1].classList).toContain('deleted');
    q('filter-restore')!.click();
    await settle();
    expect(api.restore).toHaveBeenCalledWith('f-2');
    expect(api.list).toHaveBeenCalledTimes(3);
  });

  it('previews a proposal, creates it, drops the proposal and refreshes the table', async () => {
    const { q, all, settle } = await render();
    expect(all('proposal')).toHaveLength(1);
    expect(q('proposal-pattern')!.textContent).toContain('Topic/Alpha · Act');
    q('proposal-preview')!.click();
    await settle();
    expect(api.preview).toHaveBeenCalled();
    dialog('preview-create')!.click();
    await settle();
    expect(api.create).toHaveBeenCalledWith(proposal('news@example.com').suggested);
    expect(all('proposal')).toHaveLength(0);
    expect(api.list).toHaveBeenCalledTimes(2);
  });

  it('loads more proposals', async () => {
    const { q, all, settle } = await render(undefined, paged([proposal('a@example.com')], 2));
    api.proposals.mockReturnValue(of(paged([proposal('b@example.com')], 2, 2)));
    q('proposals-more')!.click();
    await settle();
    expect(api.proposals).toHaveBeenLastCalledWith(2);
    expect(all('proposal')).toHaveLength(2);
    expect(q('proposals-more')).toBeNull();
  });

  it('opens the proposal of ?propose= after the list loads', async () => {
    const { fixture } = await render('NEWS@example.com');
    expect(api.proposals).toHaveBeenCalledWith(1);
    expect((dialog('preview-label') as HTMLInputElement).value).toBe('Topic/Alpha');
    expect(fixture.componentInstance.handled).toBe(1);
  });

  it('opens ?propose= without a proposal with just from', async () => {
    await render('other@example.com');
    expect((dialog('preview-from') as HTMLInputElement).value).toBe('other@example.com');
    expect((dialog('preview-label') as HTMLInputElement).value).toBe('');
  });
});
