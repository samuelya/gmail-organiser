import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of } from 'rxjs';
import { ExternalReviewDto, isOpenReview, sentMessage } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { ClaudeReviewerMode } from '../settings/settings.models';
import {
  ClaudeSenderActions,
  ClaudeVerdict,
  resolutionText,
  structureRows,
  verdictText,
} from './claude-verdict.component';
import {
  claudeCardTarget,
  pendingClaudeRequest,
  ReviewGroupDto,
  ReviewSenderDetailDto,
  SuggestionDto,
} from './review.models';
import { ReviewService } from './review.service';

const labels = { action: 'Act', delete: 'Bin' };

const claudeItem = (over: Partial<ExternalReviewDto> = {}): ExternalReviewDto => ({
  id: 'r1',
  targetType: 'suggestion',
  suggestionId: 's1',
  senderAddress: 'news@example.com',
  groupKey: null,
  groupDisplay: null,
  status: 'queued',
  reviewer: null,
  verdict: null,
  verdictTopicLabel: null,
  verdictNeedsAction: null,
  verdictToBeDeleted: null,
  verdictDocumentTypeLabel: null,
  verdictDocumentTypeSet: false,
  reasoning: null,
  error: null,
  resolution: 'none',
  createdAt: '2026-01-01T00:00:00Z',
  reviewedAt: null,
  resolvedAt: null,
  ...over,
});

const member = (id: string, over: Partial<SuggestionDto> = {}): SuggestionDto => ({
  id,
  messageId: `m-${id}`,
  subject: `Subject ${id}`,
  date: '2026-01-01T00:00:00Z',
  snippet: null,
  source: 'llm',
  topicLabel: 'Topic/Alpha',
  isNewLabel: false,
  needsAction: false,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.9,
  reason: '',
  status: 'pending',
  edited: false,
  protected: false,
  replaceLabels: [],
  currentLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

const group = (groupKey: string | null, members = [member('s1')]): ReviewGroupDto => ({
  groupKey,
  display: 'Synthetic group',
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
  reason: '',
  members,
  truncated: false,
  replaceLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
});

const detail = (
  groups: ReviewGroupDto[],
  page = 1,
  total = groups.length,
): ReviewSenderDetailDto => ({
  sender: {
    address: 'news@example.com',
    displayName: null,
    pending: 1,
    approved: 0,
    rejected: 0,
    applied: 0,
    totalMessages: 1,
    policyId: null,
  },
  groups,
  page,
  pageSize: 50,
  totalGroups: total,
});

function fakeClaude() {
  return {
    createReviews: vi.fn(() => of({ created: 1, skipped: 0, items: [claudeItem()] })),
    cancel: vi.fn(() => of(claudeItem({ status: 'cancelled' }))),
    accept: vi.fn(() => of(claudeItem({ status: 'reviewed', resolution: 'accepted_claude' }))),
    dismiss: vi.fn(() => of(claudeItem({ status: 'reviewed', resolution: 'dismissed' }))),
    retry: vi.fn(() => of(claudeItem())),
    copyReviewPrompt: vi.fn(() => Promise.resolve('copied')),
  };
}

describe('Claude review helpers', () => {
  it('reads the verdict and the resolution', () => {
    expect(verdictText(claudeItem({ verdict: 'agree' }), labels)).toBe('Agrees');
    expect(verdictText(claudeItem({ verdict: 'needs_human' }), labels)).toBe('Needs a human');
    expect(
      verdictText(
        claudeItem({
          verdict: 'alternative',
          verdictTopicLabel: 'Topic/Beta',
          verdictNeedsAction: true,
          verdictToBeDeleted: false,
        }),
        labels,
      ),
    ).toBe('Suggests Topic/Beta [Act]');
    const alternative = claudeItem({ verdict: 'alternative', verdictTopicLabel: 'Topic/Beta' });
    expect(
      verdictText(
        { ...alternative, verdictDocumentTypeLabel: 'Type/Invoice', verdictDocumentTypeSet: true },
        labels,
      ),
    ).toBe('Suggests Topic/Beta · Document type: Type/Invoice');
    expect(
      verdictText(
        { ...alternative, verdictDocumentTypeLabel: null, verdictDocumentTypeSet: true },
        labels,
      ),
    ).toBe('Suggests Topic/Beta · Document type: none');
    expect(
      verdictText(
        { ...alternative, verdictDocumentTypeLabel: '', verdictDocumentTypeSet: true },
        labels,
      ),
    ).toBe('Suggests Topic/Beta · Document type: none');
    expect(verdictText(alternative, labels)).toBe('Suggests Topic/Beta');
    expect(resolutionText(claudeItem({ resolution: 'accepted_claude' }))).toBe("Accepted Claude's");
    expect(resolutionText(claudeItem({ resolution: 'dismissed' }))).toBe('Kept local');
  });

  it('an item is open while queued, running or reviewed and unresolved', () => {
    expect(isOpenReview(claudeItem({ status: 'queued' }))).toBe(true);
    expect(isOpenReview(claudeItem({ status: 'running' }))).toBe(true);
    expect(isOpenReview(claudeItem({ status: 'reviewed' }))).toBe(true);
    expect(isOpenReview(claudeItem({ status: 'reviewed', resolution: 'dismissed' }))).toBe(false);
    expect(isOpenReview(claudeItem({ status: 'unavailable' }))).toBe(false);
    expect(isOpenReview(null)).toBe(false);
  });

  it('reports created and skipped', () => {
    expect(sentMessage({ created: 1, skipped: 0 })).toBe('Sent 1 item to Claude.');
    expect(sentMessage({ created: 2, skipped: 3 })).toBe('Sent 2 items to Claude; 3 already open.');
  });

  it('a card sends its group, or the one suggestion of a message analysed on its own', () => {
    expect(claudeCardTarget('news@example.com', group('key-1'))!.request).toEqual({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1' }],
    });
    const single = group(null, [member('s9', { claudeReview: claudeItem() })]);
    expect(claudeCardTarget('news@example.com', single)).toEqual({
      request: { suggestionIds: ['s9'] },
      review: claudeItem(),
    });
  });

  it('builds the pending request from groups and single messages', () => {
    expect(
      pendingClaudeRequest('news@example.com', [group('key-1'), group(null, [member('s2')])]),
    ).toEqual({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1' }],
      suggestionIds: ['s2'],
    });
    expect(pendingClaudeRequest('news@example.com', [])).toBeNull();
  });
});

describe('ClaudeVerdict', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(
    review: ExternalReviewDto | null,
    mode: ClaudeReviewerMode = 'headless_claude_code',
  ) {
    const claude = fakeClaude();
    TestBed.configureTestingModule({
      providers: [
        { provide: ClaudeService, useValue: claude },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(ClaudeVerdict);
    fixture.componentRef.setInput('mode', mode);
    fixture.componentRef.setInput('request', { suggestionIds: ['s1'] });
    fixture.componentRef.setInput('review', review);
    fixture.componentRef.setInput('labels', labels);
    const changed: ExternalReviewDto[] = [];
    fixture.componentInstance.changed.subscribe((item) => changed.push(item));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`);
    return { fixture, claude, changed, el, q };
  }

  it('is hidden when Claude review is off', async () => {
    const { q } = await render(null, 'off');
    expect(q('claude-panel')).toBeNull();
  });

  it('sends the target, reports created and skipped and emits the items', async () => {
    const { q, claude, changed } = await render(null, 'claude_desktop');
    q('claude-send')!.click();
    expect(claude.createReviews).toHaveBeenCalledWith({ suggestionIds: ['s1'] });
    expect(changed).toEqual([claudeItem()]);
    expect(document.querySelector('mat-snack-bar-container')?.textContent).toContain(
      'Sent 1 item to Claude.',
    );
  });

  it('a queued item disables Send and can be cancelled', async () => {
    const { q, claude, changed } = await render(claudeItem());
    expect(q('claude-queued')).not.toBeNull();
    expect(q('claude-send')!.disabled).toBe(true);
    q('claude-cancel')!.click();
    expect(claude.cancel).toHaveBeenCalledWith('r1');
    expect(changed[0].status).toBe('cancelled');
  });

  it('a running item shows Claude reviewing', async () => {
    const { q } = await render(claudeItem({ status: 'running' }));
    expect(q('claude-running')!.textContent).toContain('Claude is reviewing');
    expect(q('claude-cancel')).toBeNull();
  });

  it('a reviewed item shows the verdict and reasoning, and accepts or keeps local', async () => {
    const reviewed = claudeItem({
      status: 'reviewed',
      verdict: 'alternative',
      verdictTopicLabel: 'Topic/Beta',
      verdictToBeDeleted: true,
      verdictDocumentTypeLabel: 'Type/Receipt',
      verdictDocumentTypeSet: true,
      reasoning: 'Synthetic reasoning',
    });
    const { q, claude } = await render(reviewed);
    expect(q('claude-verdict')!.textContent).toContain(
      'Suggests Topic/Beta [Bin] · Document type: Type/Receipt',
    );
    expect(q('claude-reasoning')!.textContent).toContain('Synthetic reasoning');
    q('claude-accept')!.click();
    expect(claude.accept).toHaveBeenCalledWith('r1');
    q('claude-dismiss')!.click();
    expect(claude.dismiss).toHaveBeenCalledWith('r1');
  });

  it('cannot accept a needs-a-human verdict', async () => {
    const { q } = await render(claudeItem({ status: 'reviewed', verdict: 'needs_human' }));
    expect(q('claude-verdict')!.textContent).toContain('Needs a human');
    expect(q('claude-accept')!.disabled).toBe(true);
    expect(q('claude-dismiss')!.disabled).toBe(false);
  });

  it('a resolved item shows the resolution and Send again', async () => {
    const { q } = await render(
      claudeItem({ status: 'reviewed', verdict: 'agree', resolution: 'dismissed' }),
    );
    expect(q('claude-resolution')!.textContent).toContain('Kept local');
    expect(q('claude-accept')).toBeNull();
    expect(q('claude-send')!.disabled).toBe(false);
  });

  it('an unavailable item shows the error verbatim and retries', async () => {
    const { q, claude } = await render(
      claudeItem({ status: 'unavailable', error: 'Synthetic CLI error <exit 1>' }),
    );
    expect(q('claude-error')!.textContent!.trim()).toBe('Synthetic CLI error <exit 1>');
    q('claude-retry')!.click();
    expect(claude.retry).toHaveBeenCalledWith('r1');
  });

  it('a cancelled item shows nothing but Send', async () => {
    const { q } = await render(claudeItem({ status: 'cancelled' }));
    expect(q('claude-queued')).toBeNull();
    expect(q('claude-send')!.disabled).toBe(false);
  });
});

describe('ClaudeSenderActions', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(mode: ClaudeReviewerMode, pages: ReviewSenderDetailDto[]) {
    const claude = fakeClaude();
    const review = { sender: vi.fn((_a: string, _s: string, page: number) => of(pages[page - 1])) };
    TestBed.configureTestingModule({
      providers: [
        { provide: ClaudeService, useValue: claude },
        { provide: ReviewService, useValue: review },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(ClaudeSenderActions);
    fixture.componentRef.setInput('mode', mode);
    fixture.componentRef.setInput('address', 'news@example.com');
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`);
    return { fixture, claude, review, q };
  }

  it('is hidden when off; Copy prompt only in Desktop mode', async () => {
    expect((await render('off', [])).q('claude-send-pending')).toBeNull();
    TestBed.resetTestingModule();
    const headless = await render('headless_claude_code', []);
    expect(headless.q('claude-send-pending')).not.toBeNull();
    expect(headless.q('claude-copy-prompt')).toBeNull();
    TestBed.resetTestingModule();
    const desktop = await render('claude_desktop', []);
    desktop.q('claude-copy-prompt')!.click();
    expect(desktop.claude.copyReviewPrompt).toHaveBeenCalled();
  });

  it("sends every page of the sender's pending groups", async () => {
    const { q, claude, review } = await render('headless_claude_code', [
      detail([group('key-1')], 1, 51),
      detail([group(null, [member('s2')])], 2, 51),
    ]);
    q('claude-send-pending')!.click();
    expect(review.sender).toHaveBeenCalledWith('news@example.com', 'pending', 1, 50);
    expect(review.sender).toHaveBeenCalledWith('news@example.com', 'pending', 2, 50);
    expect(claude.createReviews).toHaveBeenCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1' }],
      suggestionIds: ['s2'],
    });
  });

  it('sends nothing when nothing is pending', async () => {
    const { q, claude } = await render('headless_claude_code', [detail([])]);
    q('claude-send-pending')!.click();
    expect(claude.createReviews).not.toHaveBeenCalled();
  });
});

describe('structureRows', () => {
  it('puts parents before children and keeps each label under its own parent', () => {
    expect(structureRows(['Work/Clients', 'Personal', 'Work/Projects'])).toEqual([
      { name: 'Personal', depth: 0 },
      { name: 'Work', depth: 0 },
      { name: 'Clients', depth: 1 },
      { name: 'Projects', depth: 1 },
    ]);
  });

  it('adds the parents a path implies', () => {
    expect(structureRows(['A/B/C'])).toEqual([
      { name: 'A', depth: 0 },
      { name: 'B', depth: 1 },
      { name: 'C', depth: 2 },
    ]);
  });
});
