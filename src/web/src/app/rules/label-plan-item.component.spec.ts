import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { LabelDto } from '../review/labels.models';
import { LabelPlanItem } from './label-plan-item.component';
import { LabelPlanItemDto, UpdatePlanItemRequest } from './label-plan.models';

export const planItem = (over: Partial<LabelPlanItemDto> = {}): LabelPlanItemDto => ({
  id: 'i-1',
  kind: 'nest',
  labelId: 'L1',
  labelName: 'Topic-Alpha',
  messageCount: 12,
  proposedName: 'Topic/Alpha',
  targetLabelId: null,
  targetLabelName: null,
  affectedFilterIds: [],
  rationale: 'Synthetic rationale.',
  status: 'proposed',
  error: null,
  ...over,
});

const labels: LabelDto[] = [
  { id: 'L1', name: 'Topic-Alpha', type: 'user' },
  { id: 'L2', name: 'Topic/Beta', type: 'user' },
  { id: 'INBOX', name: 'INBOX', type: 'system' },
];

@Component({
  imports: [LabelPlanItem],
  template: `<app-label-plan-item
    [item]="item()"
    [labels]="labels"
    [editable]="editable()"
    (update)="updates.push($event)"
  />`,
})
class Host {
  readonly item = signal(planItem());
  readonly editable = signal(true);
  readonly labels = labels;
  readonly updates: UpdatePlanItemRequest[] = [];
}

describe('LabelPlanItem', () => {
  async function render(item: LabelPlanItemDto, editable = true) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.item.set(item);
    fixture.componentInstance.editable.set(editable);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends HTMLElement = HTMLElement>(id: string) =>
      el.querySelector<T>(`[data-testid="${id}"]`);
    return { fixture, el, q, host: fixture.componentInstance };
  }

  it('emits accept and reject', async () => {
    const { q, host } = await render(planItem());
    q('plan-item-accept')!.querySelector('button')!.click();
    q('plan-item-reject')!.querySelector('button')!.click();
    expect(host.updates).toEqual([{ status: 'accepted' }, { status: 'rejected' }]);
  });

  it('validates the nest name and saves only a valid change', async () => {
    const { fixture, q, host } = await render(planItem());
    const input = q<HTMLInputElement>('plan-item-name')!;
    const save = q<HTMLButtonElement>('plan-item-save-name')!;
    expect(input.value).toBe('Topic/Alpha');
    expect(save.disabled).toBe(true);

    input.value = 'Topic//Alpha';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(q('plan-item-name-error')!.textContent).toContain("'/'-separated");
    expect(save.disabled).toBe(true);

    input.value = ' Topic/Gamma ';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(save.disabled).toBe(false);
    save.click();
    expect(host.updates).toEqual([{ proposedName: 'Topic/Gamma' }]);
  });

  it('picks a merge target by path and emits its id', async () => {
    const { fixture, el, q, host } = await render(
      planItem({
        kind: 'near_duplicate',
        proposedName: null,
        targetLabelId: 'L1',
        targetLabelName: 'Topic-Alpha',
      }),
    );
    expect(q('plan-item-proposal')!.textContent).toContain('→ into Topic-Alpha');
    expect(q('plan-item-name')).toBeNull();
    q('plan-item-change-target')!.click();
    await fixture.whenStable();
    el.querySelector<HTMLElement>('[data-path="Topic"]')!
      .querySelector<HTMLButtonElement>('button')!
      .click();
    await fixture.whenStable();
    el.querySelector<HTMLElement>('[data-path="Topic/Beta"]')!.click();
    expect(host.updates).toEqual([{ targetLabelId: 'L2' }]);
  });

  it('shows a failed outcome with its error and no decision controls', async () => {
    const { q } = await render(
      planItem({
        kind: 'empty',
        status: 'failed',
        error: 'Gmail refused',
        affectedFilterIds: ['f1', 'f2'],
      }),
      false,
    );
    expect(q('plan-item-status')!.textContent).toContain('failed');
    expect(q('plan-item-error')!.textContent).toContain('Gmail refused');
    expect(q('plan-item-accept')).toBeNull();
    expect(q('plan-item-proposal')!.textContent).toContain('delete');
    expect(q<HTMLAnchorElement>('plan-item-filters')!.textContent).toContain('2 filters affected');
    expect(q<HTMLAnchorElement>('plan-item-filters')!.getAttribute('href')).toContain(
      'tab=filters',
    );
  });

  it('disables the decision while not editable', async () => {
    const { q } = await render(planItem({ status: 'accepted' }), false);
    expect(q('plan-item-accept')!.querySelector('button')!.disabled).toBe(true);
    expect(q('plan-item-accept')!.querySelector('button')!.getAttribute('aria-checked')).toBe(
      'true',
    );
    expect(q('plan-item-name')).toBeNull();
  });
});
