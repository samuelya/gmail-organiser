import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { SetupState } from '../setup-state';
import { SetupStatus } from '../setup.service';
import { SummaryStep, summaryItems } from './summary-step.component';

const STEPS = { googleClient: 0, gmail: 1, ollama: 2, models: 3 };

const status = (over: Partial<SetupStatus> = {}): SetupStatus => ({
  googleClientConfigured: true,
  gmailConnected: true,
  gmailReauthRequired: false,
  ollamaReachable: false,
  chatModelSelected: false,
  embeddingModelSelected: false,
  wizardSeen: false,
  complete: false,
  ...over,
});

describe('SummaryStep', () => {
  it('maps every status item to ok or missing with its step', () => {
    const items = summaryItems(status(), STEPS);
    expect(items.map((i) => [i.ok, i.step])).toEqual([
      [true, 0],
      [true, 1],
      [false, 2],
      [false, 3],
      [false, 3],
    ]);
    expect(items[4].optional).toBe(true);
  });

  it('loads the status when it becomes active and links back to a step', async () => {
    const refresh = vi.fn(() => of(status({ chatModelSelected: true, complete: true })));
    TestBed.configureTestingModule({ providers: [{ provide: SetupState, useValue: { refresh } }] });
    const fixture = TestBed.createComponent(SummaryStep);
    fixture.componentRef.setInput('steps', STEPS);
    await fixture.whenStable();
    expect(refresh).not.toHaveBeenCalled();

    fixture.componentRef.setInput('active', true);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="state-2"]')?.textContent).toContain('Missing');
    expect(el.querySelector('[data-testid="state-3"]')?.textContent).toContain('OK');
    expect(el.querySelector('[data-testid="state-4"]')?.textContent).toContain('Optional');
    expect(el.querySelector('[data-testid="summary-verdict"]')?.textContent).toContain(
      'Setup is complete',
    );

    const goTo: number[] = [];
    fixture.componentInstance.goTo.subscribe((i) => goTo.push(i));
    el.querySelectorAll<HTMLButtonElement>('li button')[2].click();
    expect(goTo).toEqual([2]);
  });
});
