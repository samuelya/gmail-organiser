import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of } from 'rxjs';
import { AnalysisService } from '../analyse/analysis.service';
import { MAX_COMPARE } from '../analyse/compare-run';
import { JobsService } from '../core/jobs.service';
import {
  AlternativeDecision,
  AlternativeDecisionResponse,
  AlternativeTarget,
} from './alternative.models';
import { ApplySimilar, similarSource } from './apply-similar';
import { CardReanalyse } from './card-reanalyse';
import { alternative, FakeJobs, group, job, member, similar } from './review-page.testing';
import { SimilarPendingDto } from './review.models';
import { ReviewService } from './review.service';

@Component({ template: '', providers: [CardReanalyse, ApplySimilar] })
class Host {
  readonly similar = inject(ApplySimilar);
}

const accepted: AlternativeDecisionResponse = { accepted: 1, discarded: 0, skipped: 0 };
const three = similar({ count: 3, suggestionIds: ['b', 'c', 'd'], withAlternative: 1 });

describe('ApplySimilar', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(lookup: SimilarPendingDto = three) {
    const jobs = new FakeJobs();
    const review = {
      similar: vi.fn(() => of(lookup)),
      copyAlternatives: vi.fn(() => of({ copied: 3, skipped: 0 })),
      acceptAlternatives: vi.fn(() => of(accepted)),
      discardAlternatives: vi.fn(() => of(accepted)),
      job: vi.fn(() => of(job('running'))),
    };
    const analysis = { startCompareRun: vi.fn(() => of({ id: 'run-1', jobId: 'job-r' })) };
    TestBed.configureTestingModule({
      providers: [
        { provide: JobsService, useValue: jobs },
        { provide: ReviewService, useValue: review },
        { provide: AnalysisService, useValue: analysis },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    const service = fixture.componentInstance.similar;
    const copied = vi.fn();
    service.copied.subscribe(copied);
    // Twice: the dialog closes after a change detection pass.
    const settle = async () => {
      for (let i = 0; i < 2; i++) {
        fixture.detectChanges();
        await fixture.whenStable();
      }
    };
    await settle();
    /** A "Use new" (or "Keep current") on `target` that the API answers with `r`. */
    const decide = async (
      target: AlternativeTarget,
      r = accepted,
      decision: AlternativeDecision = 'accept',
    ) => {
      review.acceptAlternatives.mockReturnValue(of(r));
      review.discardAlternatives.mockReturnValue(of(r));
      service.decide(decision, target, 'news@example.com', 'pending').subscribe();
      await settle();
    };
    return { jobs, review, analysis, service, copied, settle, decide };
  }

  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';
  const snackAction = () =>
    document.querySelector<HTMLButtonElement>('mat-snack-bar-container button');
  const dialog = <T extends HTMLElement = HTMLElement>(id: string) =>
    document.querySelector<T>(`mat-dialog-container [data-testid="${id}"]`);
  const withComment = member('a', { alternative: alternative({ comment: 'These are receipts' }) });

  it('takes the member, or the card first member with a result and its comment, as the source', () => {
    expect(similarSource({ member: withComment })).toEqual({
      id: 'a',
      comment: 'These are receipts',
    });
    const card = group({
      alternative: alternative({ comment: 'Card comment' }),
      members: [member('x'), member('y', { alternative: alternative() }), member('z')],
    });
    expect(similarSource({ group: card })).toEqual({ id: 'y', comment: 'Card comment' });
    const own = group({
      alternative: alternative({ comment: 'Card comment' }),
      members: [member('y', { alternative: alternative({ comment: 'Member comment' }) })],
    });
    expect(similarSource({ group: own })).toEqual({ id: 'y', comment: 'Member comment' });
    expect(similarSource({ group: group({ members: [member('x')] }) })).toBeNull();
    expect(similarSource({ member: member('m') })).toEqual({ id: 'm', comment: '' });
  });

  it('has no source for a mixed card: it accepted several outcomes', () => {
    const mixed = group({
      alternative: alternative({ mixed: true }),
      members: [member('x', { alternative: alternative() })],
    });
    expect(similarSource({ group: mixed })).toBeNull();
  });

  it('routes Use new and Keep current, and notes a re-pended approved suggestion', async () => {
    const { review, service, decide } = await render(similar({ count: 0 }));
    await decide({ member: member('a', { alternative: alternative() }) });
    expect(review.acceptAlternatives).toHaveBeenCalledWith({ suggestionIds: ['a'] });
    expect(service.pendingAgain()).toBe(false);
    await decide({ member: withComment }, { ...accepted, discarded: 1 }, 'discard');
    expect(review.discardAlternatives).toHaveBeenCalledWith({ suggestionIds: ['a'] });
    const approved = member('p', { status: 'approved', alternative: alternative() });
    await decide({ member: approved });
    expect(service.pendingAgain()).toBe(true);
  });

  it('leaves the snackbar unchanged when nothing similar is pending, and looks nothing up on Keep current', async () => {
    const { review, decide } = await render(similar({ count: 0 }));
    await decide({ member: withComment }, { ...accepted, discarded: 1 }, 'discard');
    expect(review.similar).not.toHaveBeenCalled();
    await decide({ member: withComment });
    expect(review.similar).toHaveBeenCalledWith('a');
    expect(snackText()).toContain('Used the new result for 1 suggestion.');
    expect(snackAction()!.textContent).toContain('Dismiss');
  });

  it('looks nothing up when nothing was accepted, or for a mixed card', async () => {
    const { review, decide } = await render();
    await decide({ member: withComment }, { ...accepted, accepted: 0, skipped: 1 });
    const mixed = group({
      alternative: alternative({ mixed: true }),
      members: [member('x', { alternative: alternative() })],
    });
    await decide({ group: mixed }, { ...accepted, accepted: 2 });
    expect(review.similar).not.toHaveBeenCalled();
    expect(snackText()).toContain('Used the new result for 2 suggestions.');
  });

  it('keeps what was skipped in the offer that replaces the decision snackbar', async () => {
    const { decide } = await render();
    await decide({ member: withComment }, { ...accepted, skipped: 2 });
    expect(snackText()).toContain(
      'Accepted. 2 skipped: part of an apply that is still running. 3 similar pending emails.',
    );
  });

  it('offers Apply to similar and re-analyses the lookup ids with the comment by default', async () => {
    const { analysis, service, settle, decide } = await render();
    await decide({ member: withComment });
    expect(snackText()).toContain('Accepted. 3 similar pending emails.');
    expect(snackAction()!.textContent).toContain('Apply to similar…');
    snackAction()!.click();
    await settle();
    expect(dialog('similar-summary')!.textContent).toContain('3 pending emails from this sender.');
    expect(dialog('similar-summary')!.textContent).toContain(
      '1 already has a re-analysis result that will be replaced.',
    );
    expect(dialog<HTMLTextAreaElement>('similar-comment')!.value).toBe('These are receipts');
    dialog('similar-ok')!.click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({
      suggestionIds: ['b', 'c', 'd'],
      comment: 'These are receipts',
    });
    expect(service['reanalyse'].ids()).toEqual(new Set(['b', 'c', 'd']));
  });

  it('re-analyses without a comment when the accepted result had none', async () => {
    const { analysis, settle, decide } = await render();
    await decide({ member: member('a', { alternative: alternative() }) });
    snackAction()!.click();
    await settle();
    dialog('similar-ok')!.click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['b', 'c', 'd'] });
  });

  it('re-analyses at most one run of the lookup ids, the newest', async () => {
    const { analysis, service, settle } = await render();
    const ids = Array.from({ length: MAX_COMPARE + 1 }, (_, i) => `id-${i}`);
    service['reanalyse'].similar(ids);
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({
      suggestionIds: ids.slice(0, MAX_COMPARE),
    });
  });

  it('copies the outcome to the lookup ids, then says so and refreshes', async () => {
    const { analysis, review, copied, settle, decide } = await render();
    await decide({ member: withComment });
    snackAction()!.click();
    await settle();
    dialog('similar-mode-copy')!.querySelector('input')!.click();
    await settle();
    dialog('similar-ok')!.click();
    await settle();
    expect(review.copyAlternatives).toHaveBeenCalledWith({
      sourceSuggestionId: 'a',
      suggestionIds: ['b', 'c', 'd'],
    });
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
    expect(snackText()).toContain('Copied to 3 emails; compare them under Re-analysed');
    expect(copied).toHaveBeenCalledTimes(1);
  });

  it('says how many a copy skipped', async () => {
    const { review, service, settle } = await render();
    review.copyAlternatives.mockReturnValue(of({ copied: 0, skipped: 1 }));
    service['start']('a', ['b'], { mode: 'copy' });
    await settle();
    expect(snackText()).toContain('Copied to 0 emails; 1 email skipped; compare them');
  });

  it('starts nothing while an analysis run is active', async () => {
    const { jobs, analysis, review, settle, decide } = await render();
    await decide({ member: withComment });
    jobs.held.set([job('running', { id: 'job-x', type: 'analysis_run' })]);
    snackAction()!.click();
    await settle();
    expect(dialog('similar-ok')!.getAttribute('aria-disabled')).toBe('true');
    dialog('similar-ok')!.click();
    dialog('similar-mode-copy')!.querySelector('input')!.click();
    await settle();
    dialog('similar-ok')!.click();
    await settle();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
    expect(review.copyAlternatives).not.toHaveBeenCalled();
  });

  it('a run that became active after the confirm: a re-analysis says so, a copy goes to the API', async () => {
    const { jobs, analysis, review, service, settle } = await render();
    jobs.held.set([job('running', { id: 'job-x', type: 'analysis_run' })]);
    await settle();
    service['start']('a', ['b'], { mode: 'reanalyse' });
    await settle();
    expect(analysis.startCompareRun).not.toHaveBeenCalled();
    expect(snackText()).toContain('An analysis is running; try again once it ends.');
    service['start']('a', ['b'], { mode: 'copy' });
    await settle();
    expect(review.copyAlternatives).toHaveBeenCalledWith({
      sourceSuggestionId: 'a',
      suggestionIds: ['b'],
    });
  });
});
