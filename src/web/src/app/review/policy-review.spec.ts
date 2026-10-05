import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { SenderPolicyDto } from '../policies/policies.models';
import { PoliciesService } from '../policies/policies.service';
import { MemberRow } from './member-row.component';
import { coveringPolicy, PolicyCoverageChip } from './policy-coverage-chip.component';
import { DEFAULT_FLAG_LABELS, SuggestionDto } from './review.models';

const policy = (over: Partial<SenderPolicyDto> = {}) =>
  ({
    id: 'p-1',
    scope: 'sender',
    scopeKey: 'news@example.com',
    status: 'approved',
    ...over,
  }) as SenderPolicyDto;

describe('coveringPolicy', () => {
  it('picks the approved sender policy whose key is the address', () => {
    const other = policy({ id: 'p-0', scopeKey: 'daily-news@example.com' });
    const domain = policy({ id: 'p-2', scope: 'domain', scopeKey: 'news@example.com' });
    expect(coveringPolicy('News@Example.com', [other, domain, policy()])?.id).toBe('p-1');
    expect(coveringPolicy('news@example.com', [other, domain])).toBeNull();
    expect(coveringPolicy('news@example.com', [policy({ status: 'rejected' })])).toBeNull();
  });
});

describe('PolicyCoverageChip', () => {
  async function render(list: ReturnType<typeof vi.fn>, address = 'news@example.com') {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: PoliciesService, useValue: { list } }],
    });
    const fixture = TestBed.createComponent(PolicyCoverageChip);
    fixture.componentRef.setInput('address', address);
    await fixture.whenStable();
    const chip = () =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>(
        '[data-testid="policy-coverage"]',
      );
    return { fixture, chip };
  }

  it('links to the approved policy found by searching the address', async () => {
    const list = vi.fn(() => of({ items: [policy()], page: 1, pageSize: 20, total: 1 }));
    const { chip } = await render(list);
    expect(list).toHaveBeenCalledWith({
      status: 'approved',
      search: 'news@example.com',
      page: 1,
      pageSize: 20,
    });
    expect(chip()!.textContent).toContain('Covered by policy');
    expect(chip()!.getAttribute('href')).toBe('/policies/p-1');
  });

  it('shows nothing without a matching policy or when the lookup fails', async () => {
    const empty = await render(vi.fn(() => of({ items: [], page: 1, pageSize: 20, total: 0 })));
    expect(empty.chip()).toBeNull();

    TestBed.resetTestingModule();
    const failed = await render(vi.fn(() => throwError(() => new Error('500'))));
    expect(failed.chip()).toBeNull();
  });

  it('looks up again when the sender changes', async () => {
    const list = vi.fn((q: { search: string }) =>
      of({ items: q.search === 'news@example.com' ? [policy()] : [], total: 1 }),
    );
    const { fixture, chip } = await render(list);
    expect(chip()).not.toBeNull();
    fixture.componentRef.setInput('address', 'shop@example.com');
    await fixture.whenStable();
    expect(chip()).toBeNull();
  });
});

describe('MemberRow source', () => {
  it('labels a policy suggestion Policy', async () => {
    const fixture = TestBed.createComponent(MemberRow);
    fixture.componentRef.setInput('suggestion', {
      id: 's1',
      messageId: 'm-s1',
      subject: 'Synthetic subject',
      date: '2026-01-01T00:00:00Z',
      snippet: null,
      source: 'policy',
      topicLabel: 'Topic/Alpha',
      confidence: 1,
      reason: '',
      status: 'approved',
      replaceLabels: [],
      currentLabels: [],
      labelChange: 'add',
    } as unknown as SuggestionDto);
    fixture.componentRef.setInput('labels', DEFAULT_FLAG_LABELS);
    await fixture.whenStable();
    const source = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="member-source"]',
    );
    expect(source?.textContent?.trim()).toBe('Policy');
  });
});
