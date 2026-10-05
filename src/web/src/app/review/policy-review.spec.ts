import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { MemberRow } from './member-row.component';
import { PolicyCoverageChip } from './policy-coverage-chip.component';
import { DEFAULT_FLAG_LABELS, SuggestionDto } from './review.models';

describe('PolicyCoverageChip', () => {
  async function render(policyId: string | null) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PolicyCoverageChip);
    fixture.componentRef.setInput('policyId', policyId);
    await fixture.whenStable();
    const chip = () =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>(
        '[data-testid="policy-coverage"]',
      );
    return { fixture, chip };
  }

  it('links to the policy the API found for the sender', async () => {
    const { chip } = await render('p-1');
    expect(chip()!.textContent).toContain('Covered by policy');
    expect(chip()!.getAttribute('href')).toBe('/policies/p-1');
  });

  it('shows nothing without a policy, and follows the next sender', async () => {
    const { fixture, chip } = await render(null);
    expect(chip()).toBeNull();
    fixture.componentRef.setInput('policyId', 'p-2');
    await fixture.whenStable();
    expect(chip()!.getAttribute('href')).toBe('/policies/p-2');
    fixture.componentRef.setInput('policyId', null);
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
