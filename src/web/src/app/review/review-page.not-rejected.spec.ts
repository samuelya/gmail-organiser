import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { ALL_BUT_REJECTED_TOOLTIP } from './card-reanalyse';
import {
  alternative,
  claudeItem,
  group,
  member,
  noPattern,
  renderReviewPage as render,
} from './review-page.testing';

// One group listed once per status, as the API does on this tab.
const pendingAndApproved = () => [
  group({ status: 'pending', alternative: alternative() }),
  group({
    status: 'approved',
    members: [member('c', { status: 'approved', alternative: alternative() })],
  }),
];

describe('ReviewPage on All but rejected', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('the All but rejected tab counts every status but rejected and leaves approved cards read-only', async () => {
    const { api, el, q, all, expand, settle } = await render(
      noPattern,
      'off',
      pendingAndApproved(),
      4,
      '/?status=not_rejected',
    );
    const tabs = [...el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')];
    expect(tabs.at(-1)!.textContent?.trim()).toBe('All but rejected');
    expect(api.listSenders).toHaveBeenLastCalledWith('not_rejected', '', 1, 25, false, []);
    expect(api.sender).toHaveBeenLastCalledWith(
      'news@example.com',
      'not_rejected',
      1,
      20,
      false,
      [],
    );
    expect(all('sender-count')[0].textContent).toContain('5');
    expect(all('sender-count')[0].getAttribute('aria-label')).toBe('5 other than rejected');
    expect(q('filter-reanalysed')).toBeNull();
    expect(all('group-card')).toHaveLength(2);
    expect(all('group-approve')).toHaveLength(1);
    await expand();
    expect(all('alternative')).toHaveLength(0);
    // The approved member row keeps the actions its status allows.
    expect(all('member-reject').length).toBeGreaterThan(0);

    tabs[0].click();
    await settle();
    expect(q('filter-reanalysed')).not.toBeNull();
    expect(all('alternative').length).toBeGreaterThan(0);
  });

  it('the All but rejected tab phrases its empty state', async () => {
    const { api, q, settle } = await render();
    api.listSenders.mockReturnValue(of({ items: [], page: 1, pageSize: 25, total: 0 }));
    await TestBed.inject(Router).navigateByUrl('/?status=not_rejected');
    await settle();
    expect(q('senders-empty')!.textContent?.trim()).toBe(
      'No senders with suggestions other than rejected.',
    );
  });

  it('offers no re-analysis on the tab and points to a single-status tab', async () => {
    const { all, expand, tooltip } = await render(
      noPattern,
      'off',
      pendingAndApproved(),
      0,
      '/?status=not_rejected',
    );
    await expand();
    expect(all('group-reanalyse').every((b) => b.getAttribute('aria-disabled') === 'true')).toBe(
      true,
    );
    expect(tooltip('group-reanalyse')).toBe(ALL_BUT_REJECTED_TOOLTIP);
    expect(all('member-reanalyse')[0].getAttribute('aria-disabled')).toBe('true');
    expect(tooltip('member-reanalyse')).toBe(ALL_BUT_REJECTED_TOOLTIP);
  });

  it("keeps the user's Re-analysed choice for the single-status tabs", async () => {
    const { api, el, q, settle } = await render(noPattern, 'off', [group()], 1);
    q('filter-reanalysed')!.querySelector('button')!.click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 1, 25, true, []);
    const tabs = () => [...el.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')];
    tabs()
      .find((t) => t.textContent?.trim() === 'All but rejected')!
      .click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('not_rejected', '', 1, 25, false, []);
    tabs()[0].click();
    await settle();
    expect(api.listSenders).toHaveBeenLastCalledWith('pending', '', 1, 25, true, []);
  });

  it("shows a group's Claude review on its pending card only", async () => {
    const { jobs, el, settle } = await render(
      noPattern,
      'headless_claude_code',
      pendingAndApproved(),
      0,
      '/?status=not_rejected',
    );
    jobs.externalReviewChanges.next(
      claudeItem({ targetType: 'group', suggestionId: null, groupKey: 'key-1', status: 'queued' }),
    );
    await settle();
    const cards = el.querySelectorAll('[data-testid="group-card"]');
    expect(cards[0].querySelector('[data-testid="claude-queued"]')).not.toBeNull();
    expect(cards[1].querySelector('[data-testid="claude-queued"]')).toBeNull();
  });
});
