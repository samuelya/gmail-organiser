import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of } from 'rxjs';
import { AnalysisService } from '../analyse/analysis.service';
import { JobsService } from '../core/jobs.service';
import { AlternativeDecisionResponse } from './alternative.models';
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
    return { jobs, review, analysis, service, copied, settle };
  }

  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';
  const snackAction = () =>
    document.querySelector<HTMLButtonElement>('mat-snack-bar-container button');
  const dialog = <T extends HTMLElement = HTMLElement>(id: string) =>
    document.querySelector<T>(`mat-dialog-container [data-testid="${id}"]`);
  const withComment = member('a', { alternative: alternative({ comment: 'These are receipts' }) });

  it('takes the member, or the card first loaded member with the card comment, as the source', () => {
    expect(similarSource({ member: withComment })).toEqual({
      id: 'a',
      comment: 'These are receipts',
    });
    const card = group({
      alternative: alternative({ comment: 'Card comment' }),
      members: [member('x'), member('y')],
    });
    expect(similarSource({ group: card })).toEqual({ id: 'x', comment: 'Card comment' });
    expect(similarSource({ group: group({ members: [] }) })).toBeNull();
    expect(similarSource({ member: member('m') })).toEqual({ id: 'm', comment: '' });
  });

  it('leaves the snackbar unchanged when nothing similar is pending, and looks nothing up on Keep current', async () => {
    const { review, service, settle } = await render(similar({ count: 0 }));
    service.decided('discard', { member: withComment }, { ...accepted, discarded: 1 });
    await settle();
    expect(review.similar).not.toHaveBeenCalled();
    service.decided('accept', { member: withComment }, accepted);
    await settle();
    expect(review.similar).toHaveBeenCalledWith('a');
    expect(snackText()).toContain('Used the new result for 1 suggestion.');
    expect(snackAction()!.textContent).toContain('Dismiss');
  });

  it('looks nothing up when nothing was accepted', async () => {
    const { review, service, settle } = await render();
    service.decided('accept', { member: withComment }, { ...accepted, accepted: 0, skipped: 1 });
    await settle();
    expect(review.similar).not.toHaveBeenCalled();
  });

  it('offers Apply to similar and re-analyses the lookup ids with the comment by default', async () => {
    const { analysis, service, settle } = await render();
    service.decided('accept', { member: withComment }, accepted);
    await settle();
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
    const { analysis, service, settle } = await render();
    service.decided('accept', { member: member('a', { alternative: alternative() }) }, accepted);
    await settle();
    snackAction()!.click();
    await settle();
    dialog('similar-ok')!.click();
    await settle();
    expect(analysis.startCompareRun).toHaveBeenCalledWith({ suggestionIds: ['b', 'c', 'd'] });
  });

  it('copies the outcome to the lookup ids, then says so and refreshes', async () => {
    const { analysis, review, service, copied, settle } = await render();
    service.decided('accept', { member: withComment }, accepted);
    await settle();
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

  it('starts nothing while an analysis run is active', async () => {
    const { jobs, analysis, review, service, settle } = await render();
    service.decided('accept', { member: withComment }, accepted);
    await settle();
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
});
