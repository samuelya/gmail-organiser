import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import {
  alternative,
  claudeItem,
  detail,
  group,
  job,
  member,
  noPattern,
  renderReviewPage as render,
  sender,
  similar,
} from './review-page.testing';
import { ReviewSenderDto } from './review.models';

describe('ReviewPage', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  const dialogButton = (testId: string) =>
    document.querySelector<HTMLButtonElement>(`mat-dialog-container [data-testid="${testId}"]`)!;
  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';

  it('lists senders, selects the first and renders its groups', async () => {
    const { api, q, all } = await render();
    expect(api.listSenders).toHaveBeenCalledWith('pending', '', 1, 25, false, []);
    expect(all('sender-item')).toHaveLength(2);
    expect(all('sender-count')[0].textContent).toContain('3');
    expect(api.sender).toHaveBeenCalledWith('news@example.com', 'pending', 1, 20, false, []);
    expect(q('detail-title')!.textContent).toContain('Example News');
    expect(q('group-title')!.textContent).toContain('Weekly digest');
    expect(q('group-origin')!.textContent).toContain('1 analysed by the model · 2 derived');
    expect(q('group-new-label')).not.toBeNull();
    expect(q('group-delete')!.textContent).toContain('Bin');
    expect(q('group-confidence')!.textContent).toContain('70%–90%');
    expect(q('group-mixed')).not.toBeNull();
    expect(q('apply-approved')!.textContent).toContain('Apply approved (2)');
    expect(q('apply-rest')).toBeNull();
  });

  it('re-fetches the detail for another sender and status', async () => {
    const { api, all, el, settle } = await render();
    all('sender-item')[1].click();
    await settle();
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'pending', 1, 20, false, []);
    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[1].click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('approved', '', 1, 25, false, []);
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'approved', 1, 20, false, []);
  });

  it('approves and rejects a member, then re-fetches', async () => {
    const { api, all, expand, settle } = await render();
    await expand();
    expect(all('member-row')).toHaveLength(2);
    expect(all('member-source')[1].textContent).toContain('derived');
    expect(all('member-protected')).toHaveLength(1);
    const calls = api.sender.mock.calls.length;
    all('member-approve')[0].click();
    await settle();
    expect(api.approve).toHaveBeenCalledWith('a');
    all('member-reject')[1].click();
    await settle();
    expect(api.reject).toHaveBeenCalledWith('b');
    expect(api.sender.mock.calls.length).toBe(calls + 2);
  });

  it("approves a group with the card's outcome and shows the skipped members", async () => {
    const { api, q, all, expand, settle } = await render(noPattern, 'off', [
      group({ documentTypeLabel: 'Docs/Invoice' }),
    ]);
    q('group-approve')!.click();
    await settle();
    expect(api.approveGroup).toHaveBeenCalledWith('news@example.com', 'key-1', {
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: true,
      documentTypeLabel: 'Docs/Invoice',
      extraLabels: [],
    });
    expect(snackText()).toContain('1 member was skipped');
    await expand();
    expect(all('member-skipped')).toHaveLength(1);
    q('group-reject')!.click();
    await settle();
    expect(api.rejectGroup).toHaveBeenCalledWith('news@example.com', 'key-1');
  });

  it('decides a message analysed on its own by its suggestion', async () => {
    const { api, q, settle } = await render();
    api.sender.mockReturnValue(
      of(detail([group({ groupKey: null, size: 1, members: [member('solo')] })])),
    );
    q('group-reject')!.click();
    await settle();
    q('group-approve')!.click();
    await settle();
    expect(api.approve).toHaveBeenCalledWith('solo');
    expect(api.approveGroup).not.toHaveBeenCalled();
  });

  it('opens the edit hook for a group and re-fetches when saved', async () => {
    const { api, edit, q, settle } = await render();
    const calls = api.sender.mock.calls.length;
    q('group-edit')!.click();
    await settle();
    expect(edit.editGroup).toHaveBeenCalledWith(
      'news@example.com',
      expect.objectContaining({ groupKey: 'key-1' }),
    );
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  it('analyses the selected members individually', async () => {
    const { api, q, el, expand, settle } = await render();
    await expand();
    el.querySelectorAll<HTMLInputElement>('[data-testid="member-select"] input').forEach((i) =>
      i.click(),
    );
    await settle();
    expect(q('analyse-individually')!.textContent).toContain('(2)');
    q('analyse-individually')!.click();
    await settle();
    expect(api.analyseIndividually).toHaveBeenCalledWith(['a', 'b']);
    expect(snackText()).toContain('Analysing 2 messages individually');
    expect(q('analyse-individually')!.textContent).toContain('(0)');
  });

  it('applies approved and follows the job from the hub until it completes', async () => {
    const { api, jobs, q, settle } = await render();
    q('apply-approved')!.click();
    await settle();
    expect(api.apply).toHaveBeenCalledWith('news@example.com');
    expect(q('apply-progress')).not.toBeNull();
    jobs.held.set([job('running')]);
    await settle();
    expect(q('apply-message')!.textContent).toContain('Applied 2 of 4 messages');
    const calls = api.sender.mock.calls.length;
    jobs.held.set([
      job('completed', {
        version: 2,
        progress: {
          done: 3,
          total: 4,
          message: 'Applied 3 of 4 messages; 1 skipped (label refused)',
        },
      }),
    ]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    expect(snackText()).toContain('1 skipped (label refused)');
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  it('reads the apply job from the API after a reconnect', async () => {
    const { api, jobs, q, settle } = await render();
    q('apply-approved')!.click();
    await settle();
    jobs.reconnects.set(1);
    await settle();
    expect(api.job).toHaveBeenCalledWith('job-1');
    expect(q('apply-progress')).toBeNull();
  });

  it('picks up an apply job already running on load and blocks a second apply', async () => {
    const { api, jobs, q, settle } = await render();
    jobs.held.set([job('running', { id: 'job-9', type: 'mailbox_fetch' })]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    jobs.held.set([job('paused', { id: 'job-7' })]);
    await settle();
    expect(q('apply-message')!.textContent).toContain('Applied 2 of 4 messages');
    expect((q('apply-approved') as HTMLButtonElement).disabled).toBe(true);
    q('apply-approved')!.click();
    await settle();
    expect(api.apply).not.toHaveBeenCalled();
    q('apply-cancel')!.click();
    await settle();
    dialogButton('confirm-ok').click();
    await settle();
    expect(jobs.cancel).toHaveBeenCalledWith('job-7');
    jobs.held.set([job('cancelled', { id: 'job-7', version: 2 })]);
    await settle();
    expect(q('apply-progress')).toBeNull();
    expect((q('apply-approved') as HTMLButtonElement).disabled).toBe(false);
  });

  it('picks up an apply job from the snapshot after a reconnect', async () => {
    const { jobs, q, settle } = await render();
    jobs.reconnects.set(1);
    jobs.held.set([job('queued', { id: 'job-8' })]);
    await settle();
    expect(q('apply-progress')).not.toBeNull();
  });

  it('applies to the rest of the sender after confirming the pattern', async () => {
    const { api, jobs, q, settle } = await render({
      topicLabel: 'Topic/Alpha',
      needsAction: true,
      toBeDeleted: false,
      approvals: 4,
      agreement: 0.75,
      remaining: 6,
      documentTypeLabel: null,
    });
    expect(q('apply-rest')!.textContent).toContain('remaining 6');
    q('apply-rest')!.click();
    await settle();
    const dialog = document.querySelector('mat-dialog-container')!.textContent!;
    expect(dialog).toContain('label "Topic/Alpha", "Act"');
    expect(dialog).toContain('A Gmail filter for this sender can be created afterwards');
    dialogButton('confirm-ok').click();
    await settle();
    await settle();
    expect(api.applyRest).toHaveBeenCalledWith('news@example.com', {});
    expect(q('apply-progress')).not.toBeNull();
    expect(snackText()).toContain('Created 4 suggestions');
    // The apply job's completion snackbar replaces this one, so it carries "Create filter".
    jobs.held.set([job('completed', { id: 'job-2', version: 2 })]);
    await settle();
    await settle();
    expect(snackText()).toContain('Applied 2 of 4 messages');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const action = document.querySelector<HTMLButtonElement>('mat-snack-bar-container button')!;
    expect(action.textContent).toContain('Create filter');
    action.click();
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/rules'], {
      queryParams: { propose: 'news@example.com' },
    });
  });

  it("names the pattern's document type without a parent in settings and does not re-send it", async () => {
    const { api, q, settle } = await render({
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: false,
      approvals: 4,
      agreement: 0.75,
      remaining: 6,
      documentTypeLabel: 'Docs/Invoice',
    });
    q('apply-rest')!.click();
    await settle();
    const dialog = document.querySelector('mat-dialog-container')!.textContent!;
    expect(dialog).toContain('label "Topic/Alpha", document type "Docs/Invoice"');
    dialogButton('confirm-ok').click();
    await settle();
    expect(api.applyRest).toHaveBeenCalledWith('news@example.com', {});
  });

  it('does not apply to the rest when the confirm is cancelled', async () => {
    const { api, q, settle } = await render({
      ...noPattern,
      topicLabel: 'Topic/Alpha',
      needsAction: false,
      toBeDeleted: false,
    });
    q('apply-rest')!.click();
    await settle();
    dialogButton('confirm-cancel').click();
    await settle();
    expect(api.applyRest).not.toHaveBeenCalled();
  });

  it('hides every Claude action and hint when Claude review is off', async () => {
    const { q, expand } = await render(noPattern, 'off', [
      group({ suggestedForClaude: true, members: [member('a', { suggestedForClaude: true })] }),
    ]);
    await expand();
    expect(q('claude-panel')).toBeNull();
    expect(q('claude-send-pending')).toBeNull();
    expect(q('group-claude-hint')).toBeNull();
    expect(q('member-claude-hint')).toBeNull();
  });

  it('shows the Claude? hint only on items suggested for Claude', async () => {
    const { q, all, expand } = await render(noPattern, 'headless_claude_code', [
      group({
        suggestedForClaude: true,
        members: [member('a', { suggestedForClaude: true }), member('b')],
      }),
    ]);
    await expand();
    expect(q('group-claude-hint')!.textContent).toContain('Claude?');
    expect(all('member-claude-hint')).toHaveLength(1);
  });

  it('sends a group card and a member row to Claude', async () => {
    const { claude, all, expand } = await render(noPattern, 'headless_claude_code');
    await expand();
    const sends = all('claude-send');
    expect(sends).toHaveLength(3);
    sends[0].click();
    expect(claude.createReviews).toHaveBeenLastCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1' }],
    });
    sends[2].click();
    expect(claude.createReviews).toHaveBeenLastCalledWith({ suggestionIds: ['b'] });
  });

  it('a hub change patches the matching row in place', async () => {
    const { jobs, api, el, settle, expand } = await render(noPattern, 'headless_claude_code');
    await expand();
    const calls = api.sender.mock.calls.length;
    jobs.externalReviewChanges.next(claudeItem());
    jobs.externalReviewChanges.next(
      claudeItem({
        id: 'r2',
        targetType: 'group',
        suggestionId: null,
        groupKey: 'key-1',
        status: 'queued',
      }),
    );
    await settle();
    const rows = el.querySelectorAll('[data-testid="member-row"]');
    expect(rows[0].querySelector('[data-testid="claude-running"]')).toBeNull();
    expect(rows[1].querySelector('[data-testid="claude-running"]')).not.toBeNull();
    expect(el.querySelectorAll('[data-testid="claude-queued"]')).toHaveLength(1);
    expect(api.sender.mock.calls.length).toBe(calls);
  });

  it('re-fetches once an item is accepted, since its suggestions changed', async () => {
    const { jobs, api, settle } = await render(noPattern, 'headless_claude_code');
    const calls = api.sender.mock.calls.length;
    jobs.externalReviewChanges.next(
      claudeItem({ status: 'reviewed', verdict: 'agree', resolution: 'accepted_claude' }),
    );
    await settle();
    expect(api.sender.mock.calls.length).toBe(calls + 1);
  });

  it('re-analyses the ticked members after confirming, then clears the selection', async () => {
    const { analysis, q, expand, tick, settle } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b', { status: 'applied' })] }),
    ]);
    await expand();
    await tick(0, 1);
    expect(q('reanalyse-selection')!.textContent).toContain('Re-analyse (2)');
    q('reanalyse-selection')!.click();
    await settle();
    const text = document.querySelector('mat-dialog-container')!.textContent!;
    expect(text).toContain('Re-analyse 2 emails?');
    expect(text).toContain('memory shortcut is skipped');
    dialogButton('confirm-ok').click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['a', 'b'] });
    expect(snackText()).toContain('Re-analysing 2 messages again');
    await settle();
    expect(q('reanalyse-selection')!.textContent).toContain('(0)');
  });

  it('a mail-type change reloads senders from page 1 with the filter and clears the ticks', async () => {
    const { fixture, api, q, expand, tick, settle } = await render(noPattern, 'off', [
      group({ members: [member('a', { mailType: 'newsletter' })] }),
    ]);
    const page = fixture.componentInstance;
    page.onSenderPage(2);
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 2, 25, false, []);
    await expand();
    await tick(0);
    expect(q('reanalyse-selection')!.textContent).toContain('(1)');
    page.onMailTypes(['receipt', 'newsletter']);
    await settle();
    await settle();
    expect(TestBed.inject(Router).url).toContain('mailType=receipt,newsletter');
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 1, 25, false, [
      'receipt',
      'newsletter',
    ]);
    expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, false, [
      'receipt',
      'newsletter',
    ]);
    expect(page.selection().size).toBe(0);
    expect(q('mail-type-page-note')).toBeNull();
  });

  it('lists a matching group whole, each member with its own mail-type chip', async () => {
    const { fixture, el, all, expand, settle } = await render(noPattern, 'off', [
      group({
        members: [member('a', { mailType: 'receipt' }), member('b', { mailType: 'newsletter' })],
      }),
    ]);
    fixture.componentInstance.onMailTypes(['receipt']);
    await settle();
    await settle();
    await expand();
    expect(all('member-row')).toHaveLength(2);
    const chips = [
      ...el.querySelectorAll('[data-testid="member-row"] [data-testid="mail-type-chip"]'),
    ];
    expect(chips.map((c) => c.textContent!.trim())).toHaveLength(2);
    expect(chips[0].textContent).not.toBe(chips[1].textContent);
  });

  it('shows the empty state when the selected sender has nothing of the selected types', async () => {
    const { fixture, api, q, settle } = await render();
    api.sender.mockImplementation(() =>
      throwError(() => new HttpErrorResponse({ status: 404, statusText: 'Not Found' })),
    );
    fixture.componentInstance.onMailTypes(['receipt']);
    await settle();
    await settle();
    expect(q('detail-empty')!.textContent).toContain(
      'No pending suggestions of the selected mail types for this sender.',
    );
    expect(q('detail-none')).toBeNull();
  });

  it('both selection buttons are disabled while a re-analyse is starting', async () => {
    const { q, expand, tick, settle } = await render();
    await expand();
    await tick(0);
    q('reanalyse-selection')!.click();
    await settle();
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).toBe('true');
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).toBe('true');
    dialogButton('confirm-cancel').click();
    await settle();
    await settle();
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).not.toBe('true');
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('an applied member can be ticked; Analyse individually then explains it is blocked', async () => {
    const { api, q, expand, tick, tooltip } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b', { status: 'applied' })] }),
    ]);
    await expand();
    await tick(1);
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('analyse-individually')).toBe('Applied emails can only be re-analysed');
    q('analyse-individually')!.click();
    expect(api.analyseIndividually).not.toHaveBeenCalled();
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('a reload drops ticks on members no longer shown, so an applied one cannot stay ticked', async () => {
    const { api, q, all, expand, tick, settle } = await render(noPattern, 'off', [
      group({ members: [member('a'), member('b')] }),
    ]);
    await expand();
    await tick(0, 1);
    expect(q('analyse-individually')!.textContent).toContain('(2)');
    // "b" was applied meanwhile: the pending tab no longer lists it.
    api.sender.mockReturnValue(of(detail([group({ members: [member('a')] })])));
    all('member-approve')[0].click();
    await settle();
    expect(q('analyse-individually')!.textContent).toContain('(1)');
    expect(q('analyse-individually')!.getAttribute('aria-disabled')).not.toBe('true');
  });

  it('Re-analyse is disabled with a tooltip while an analysis run is queued or running', async () => {
    const { jobs, analysis, q, expand, tick, settle, tooltip } = await render();
    await expand();
    await tick(0);
    jobs.held.set([job('queued', { id: 'job-a', type: 'analysis_run' })]);
    await settle();
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('reanalyse-selection')).toBe('An analysis is running');
    q('reanalyse-selection')!.click();
    await settle();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
  });

  it('filters by Re-analysed with the count from the summary', async () => {
    const { api, el, q, settle } = await render(noPattern, 'off', [group()], 4, '/', 7);
    expect(q('filter-reanalysed')!.textContent).toContain('Re-analysed (4)');
    expect(q('apply-all')!.textContent).toContain('Apply all approved (7)');
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 1, 25, true, []);
    expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, true, []);
  });

  it('hides the Re-analysed filter while nothing waits, and Apply all once a reload fails', async () => {
    const { analysis, jobs, q, settle } = await render(noPattern, 'off', [group()], 0, '/', 3);
    expect(q('filter-reanalysed')).toBeNull();
    analysis.summary.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    jobs.reconnects.set(1);
    await settle();
    expect(q('apply-all')).toBeNull();
  });

  it('Use new on an approved member notes it is pending again; Keep current on a card names the group and tab', async () => {
    const alt = alternative();
    const { api, q, all, expand, settle } = await render(noPattern, 'off', [
      group({
        alternative: alternative({ count: 1 }),
        members: [member('a', { status: 'approved', alternative: alt }), member('b')],
      }),
    ]);
    await expand();
    const compares = all('alternative');
    expect(compares).toHaveLength(2);
    compares[1].querySelector<HTMLButtonElement>('[data-testid="alternative-use"]')!.click();
    await settle();
    expect(api.acceptAlternatives).toHaveBeenCalledWith({ suggestionIds: ['a'] });
    expect(snackText()).toContain('Used the new result for 1 suggestion.');
    expect(q('pending-again')!.textContent).toContain(
      'pending again: approve it and apply it to change Gmail',
    );

    compares[0].querySelector<HTMLButtonElement>('[data-testid="alternative-keep"]')!.click();
    await settle();
    expect(api.discardAlternatives).toHaveBeenCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1', status: 'pending' }],
    });
  });

  it('Use new looks up similar pending emails of the member, or of the card first member, and offers them', async () => {
    const { api, all, expand, settle } = await render(noPattern, 'off', [
      group({
        alternative: alternative({ count: 2 }),
        members: [
          member('a', { alternative: alternative() }),
          member('b', { alternative: alternative() }),
        ],
      }),
    ]);
    await expand();
    all('alternative-use')[2].click();
    await settle();
    expect(api.similar).toHaveBeenLastCalledWith('b');
    expect(snackText()).toContain('Used the new result for 1 suggestion.');
    api.similar.mockReturnValue(of(similar({ count: 2, suggestionIds: ['c', 'd'] })));
    all('alternative-use')[0].click();
    await settle();
    expect(api.similar).toHaveBeenLastCalledWith('a');
    expect(snackText()).toContain('Accepted. 2 similar pending emails.');
    expect(snackText()).toContain('Apply to similar…');
  });

  it('Use new on a pending member adds no note', async () => {
    const { q, all, expand, settle } = await render(noPattern, 'off', [
      group({ members: [member('a', { alternative: alternative() }), member('b')] }),
    ]);
    await expand();
    all('alternative-use')[0].click();
    await settle();
    expect(q('pending-again')).toBeNull();
  });

  it('the Applied tab is read-only except ticks, Re-analyse and Use new / Keep current', async () => {
    const applied = (id: string) => member(id, { status: 'applied', alternative: alternative() });
    const { api, el, q, all, expand, tick, settle } = await render(noPattern, 'off', [
      group({ alternative: alternative({ count: 2 }), members: [applied('a'), applied('b')] }),
    ]);
    const tabs = el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button');
    expect([...tabs].map((t) => t.textContent?.trim())).toContain('Applied');
    tabs[3].click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('applied', '', 1, 25, false, []);
    expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'applied', 1, 20, false, []);
    await expand();
    for (const id of ['group-approve', 'group-reject', 'group-edit']) expect(q(id)).toBeNull();
    for (const id of ['member-approve', 'member-reject', 'member-edit'])
      expect(all(id)).toHaveLength(0);
    await tick(0);
    expect(q('reanalyse-selection')!.getAttribute('aria-disabled')).not.toBe('true');
    all('alternative-use')[0].click();
    await settle();
    expect(api.acceptAlternatives).toHaveBeenCalledWith({
      groups: [{ senderAddress: 'news@example.com', groupKey: 'key-1', status: 'applied' }],
    });
    expect(q('pending-again')).not.toBeNull();
  });

  it('a tab or Re-analysed change selects the first listed sender when the selected one is not listed', async () => {
    const { api, el, settle } = await render(noPattern, 'off', [group()], 4);
    const only = (address: string) =>
      of({ items: [sender({ address })], page: 1, pageSize: 25, total: 1 });
    // The fifth argument is the Re-analysed filter.
    api.listSenders.mockImplementation((...args: unknown[]) =>
      only(args[4] ? 'shop@example.com' : 'news@example.com'),
    );
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'pending', 1, 20, true, []);
    api.listSenders.mockImplementation(() => of({ items: [], page: 1, pageSize: 25, total: 0 }));
    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[1].click();
    await settle();
    expect(el.querySelector('[data-testid="detail-none"]')).not.toBeNull();
  });

  it('a Re-analysed change never requests the detail of a sender the new list hides', async () => {
    const { api, el, settle } = await render(noPattern, 'off', [group()], 4);
    const filtered = new Subject<{
      items: ReviewSenderDto[];
      page: number;
      pageSize: number;
      total: number;
    }>();
    api.listSenders.mockImplementation((...args: unknown[]) =>
      args[4] ? filtered : of({ items: [sender()], page: 1, pageSize: 25, total: 1 }),
    );
    api.sender.mockClear();
    el.querySelector<HTMLButtonElement>('[data-testid="filter-reanalysed"] button')!.click();
    await settle();
    expect(api.sender).not.toHaveBeenCalled();
    filtered.next({
      items: [sender({ address: 'shop@example.com' })],
      page: 1,
      pageSize: 25,
      total: 1,
    });
    await settle();
    expect(api.sender).toHaveBeenCalledTimes(1);
    expect(api.sender).toHaveBeenCalledWith('shop@example.com', 'pending', 1, 20, true, []);
  });

  it('opens with the tab and sender the URL names, even a sender off the first page', async () => {
    const { api } = await render(
      noPattern,
      'off',
      [group()],
      0,
      '/?status=approved&sender=%20Other@Example.com%20&mailType=receipt',
    );
    expect(api.listSenders).toHaveBeenCalledWith('approved', '', 1, 25, false, ['receipt']);
    // Off the first page: a search checks the API lists it under this tab before its detail is requested.
    expect(api.listSenders).toHaveBeenCalledWith('approved', 'Other@Example.com', 1, 25, false, [
      'receipt',
    ]);
    expect(api.sender).toHaveBeenCalledTimes(1);
    expect(api.sender).toHaveBeenCalledWith('other@example.com', 'approved', 1, 20, false, [
      'receipt',
    ]);
    expect(TestBed.inject(Router).url).toContain('sender=other@example.com');
  });

  it('a stale link selects the first listed sender without requesting the unlisted one', async () => {
    const { api } = await render(noPattern, 'off', [group()], 0, '/?sender=gone@example.com');
    expect(api.sender).toHaveBeenCalledTimes(1);
    expect(api.sender).toHaveBeenCalledWith('news@example.com', 'pending', 1, 20, false, []);
    expect(TestBed.inject(Router).url).toBe('/?sender=news@example.com');
  });

  it('a link to an unlisted sender while the page is open keeps the detail it can show', async () => {
    const { api, settle } = await render();
    await TestBed.inject(Router).navigateByUrl('/?sender=gone@example.com');
    await settle();
    expect(api.sender).not.toHaveBeenCalledWith(
      'gone@example.com',
      expect.anything(),
      1,
      20,
      false,
      [],
    );
    expect(TestBed.inject(Router).url).toBe('/?sender=news@example.com');
  });

  it('an unknown status is written back as pending', async () => {
    await render(noPattern, 'off', [group()], 0, '/?status=bogus');
    expect(TestBed.inject(Router).url).toBe('/?status=pending&sender=news@example.com');
  });

  it('a tab change writes the status, drops the sender and keeps the mail types', async () => {
    const { api, el, settle } = await render(
      noPattern,
      'off',
      [group()],
      0,
      '/?mailType=receipt&sender=shop@example.com',
    );
    el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')[2].click();
    await settle();
    const url = TestBed.inject(Router).url;
    expect(url).toContain('status=rejected');
    expect(url).toContain('mailType=receipt');
    // The new list still has the sender, so the entry names it again.
    expect(url).toContain('sender=shop@example.com');
    expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'rejected', 1, 20, false, [
      'receipt',
    ]);
  });

  describe('history', () => {
    // The router handles a popstate on the next macrotask.
    const go = async (settle: () => Promise<void>, step: 'back' | 'forward') => {
      TestBed.inject(Location)[step]();
      await new Promise((resolve) => setTimeout(resolve));
      await settle();
    };

    it('a sender selection is a navigation: Back restores each sender the entries showed', async () => {
      const { api, all, settle } = await render();
      expect(TestBed.inject(Router).url).toBe('/?sender=news@example.com');
      all('sender-item')[1].click();
      await settle();
      expect(TestBed.inject(Router).url).toBe('/?sender=shop@example.com');
      all('sender-item')[0].click();
      await settle();
      expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, false, []);
      await go(settle, 'back');
      expect(api.sender).toHaveBeenLastCalledWith('shop@example.com', 'pending', 1, 20, false, []);
      await go(settle, 'back');
      expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, false, []);
    });

    it('Forward to another tab keeps the sender that entry named, even off the first page', async () => {
      const { api, settle } = await render();
      await TestBed.inject(Router).navigateByUrl('/?status=approved&sender=other@example.com');
      await settle();
      expect(api.sender).toHaveBeenLastCalledWith(
        'other@example.com',
        'approved',
        1,
        20,
        false,
        [],
      );
      await go(settle, 'back');
      expect(api.sender).toHaveBeenLastCalledWith('news@example.com', 'pending', 1, 20, false, []);
      await go(settle, 'forward');
      expect(api.sender).toHaveBeenLastCalledWith(
        'other@example.com',
        'approved',
        1,
        20,
        false,
        [],
      );
      expect(TestBed.inject(Router).url).toBe('/?status=approved&sender=other@example.com');
    });

    it('Back to a sender no longer listed selects the first listed one, without a 404', async () => {
      const { api, all, settle, fixture } = await render();
      all('sender-item')[1].click();
      await settle();
      all('sender-item')[0].click();
      await settle();
      // The shop's suggestions were all approved meanwhile: a search no longer finds it under Pending.
      api.listSenders.mockImplementation(() =>
        of({ items: [sender()], page: 1, pageSize: 25, total: 1 }),
      );
      fixture.componentInstance.senders.set({ items: [sender()], page: 1, pageSize: 25, total: 1 });
      api.sender.mockClear();
      await go(settle, 'back');
      expect(api.sender).not.toHaveBeenCalledWith('shop@example.com', 'pending', 1, 20, false, []);
      expect(TestBed.inject(Router).url).toBe('/?sender=news@example.com');
    });
  });
});
