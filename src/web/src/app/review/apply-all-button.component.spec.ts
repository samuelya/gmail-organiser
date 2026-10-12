import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of, throwError } from 'rxjs';
import { ApplyAllButton } from './apply-all-button.component';
import { ApplyTracker } from './apply-tracker';
import { ReviewService } from './review.service';

describe('ApplyAllButton', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(count: number) {
    const api = { applyAll: vi.fn(() => of({ id: 'batch-3', jobId: 'job-3' })) };
    const jobId = signal<string | null>(null);
    const apply = { jobId, track: vi.fn((id: string | null) => jobId.set(id)) };
    TestBed.configureTestingModule({
      providers: [
        { provide: ReviewService, useValue: api },
        { provide: ApplyTracker, useValue: apply },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(ApplyAllButton);
    fixture.componentRef.setInput('count', count);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    await settle();
    const button = () =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
        '[data-testid="apply-all"]',
      );
    const confirm = async (answer: 'confirm-ok' | 'confirm-cancel') => {
      button()!.click();
      await settle();
      document
        .querySelector<HTMLButtonElement>(`mat-dialog-container [data-testid="${answer}"]`)!
        .click();
      await settle();
      await settle();
    };
    return { fixture, api, apply, changed, button, confirm, settle };
  }

  const dialogText = () => document.querySelector('mat-dialog-container')?.textContent ?? '';
  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';

  it('is hidden when nothing is approved', async () => {
    const { button } = await render(0);
    expect(button()).toBeNull();
  });

  it('confirms with the count, applies, follows the job and asks for a re-fetch', async () => {
    const { api, apply, changed, button, settle } = await render(7);
    expect(button()!.textContent).toContain('Apply all approved (7)');
    button()!.click();
    await settle();
    expect(dialogText()).toContain('Apply all approved suggestions?');
    expect(dialogText()).toContain(
      '7 approved suggestions from every sender are applied to Gmail.',
    );
    document
      .querySelector<HTMLButtonElement>('mat-dialog-container [data-testid="confirm-ok"]')!
      .click();
    await settle();
    await settle();
    expect(api.applyAll).toHaveBeenCalledTimes(1);
    expect(apply.track).toHaveBeenCalledWith('job-3');
    expect(changed).toHaveBeenCalledTimes(1);
    expect(button()!.disabled).toBe(true);
  });

  it('says "suggestion is" for one', async () => {
    const { button, settle } = await render(1);
    button()!.click();
    await settle();
    expect(dialogText()).toContain('1 approved suggestion from every sender is applied');
  });

  it('does nothing when the confirm is cancelled', async () => {
    const { api, changed, confirm } = await render(3);
    await confirm('confirm-cancel');
    expect(api.applyAll).not.toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();
  });

  it('explains a 409: the approved suggestions left have labels Gmail refuses', async () => {
    const { api, apply, changed, button, confirm } = await render(3);
    api.applyAll.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    await confirm('confirm-ok');
    expect(apply.track).not.toHaveBeenCalled();
    expect(changed).toHaveBeenCalledTimes(1);
    expect(snackText()).toContain('labels Gmail refuses');
    expect(button()!.disabled).toBe(false);
  });

  it('is disabled while another action or an apply job runs', async () => {
    const { fixture, apply, button, settle } = await render(3);
    fixture.componentRef.setInput('disabled', true);
    await settle();
    expect(button()!.disabled).toBe(true);
    fixture.componentRef.setInput('disabled', false);
    apply.jobId.set('job-1');
    await settle();
    expect(button()!.disabled).toBe(true);
  });
});
