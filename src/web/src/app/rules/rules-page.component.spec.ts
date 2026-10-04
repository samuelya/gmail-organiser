import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { FiltersTab } from './filters-tab.component';
import { FindingsTab } from './findings-tab.component';
import { LabelsTab } from './labels-tab.component';
import { RulesPage } from './rules-page.component';

@Component({
  selector: 'app-filters-tab',
  template: '<p data-testid="filters-stub">{{ propose() }}</p>',
})
class FiltersTabStub {
  readonly propose = input<string>();
  readonly proposeHandled = output<void>();
}

@Component({ selector: 'app-findings-tab', template: '<p data-testid="findings-stub"></p>' })
class FindingsTabStub {}

@Component({ selector: 'app-labels-tab', template: '<p data-testid="labels-stub"></p>' })
class LabelsTabStub {}

describe('RulesPage', () => {
  async function render(url: string) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'rules', component: RulesPage }], withComponentInputBinding()),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    TestBed.overrideComponent(RulesPage, {
      remove: { imports: [FiltersTab, FindingsTab, LabelsTab] },
      add: { imports: [FiltersTabStub, FindingsTabStub, LabelsTabStub] },
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    const el = harness.routeNativeElement as HTMLElement;
    const tabs = () => [...el.querySelectorAll<HTMLElement>('[role="tab"]')];
    const selected = () => tabs().find((t) => t.getAttribute('aria-selected') === 'true')!;
    return { harness, el, tabs, selected, router: TestBed.inject(Router) };
  }

  it('opens the tab named by ?tab=', async () => {
    const { el, selected } = await render('/rules?tab=findings');
    expect(selected().textContent).toContain('Findings');
    expect(el.querySelector('[data-testid="findings-stub"]')).not.toBeNull();
  });

  it('defaults to Filters and passes ?propose= on', async () => {
    const { el, selected } = await render('/rules?tab=nope&propose=news@example.com');
    expect(selected().textContent).toContain('Filters');
    expect(el.querySelector('[data-testid="filters-stub"]')!.textContent).toContain(
      'news@example.com',
    );
  });

  it('writes the selected tab to the query string', async () => {
    const { harness, tabs, selected, router } = await render('/rules');
    tabs()[2].click();
    await harness.fixture.whenStable();
    expect(router.url).toBe('/rules?tab=labels');
    expect(selected().textContent).toContain('Labels');
    expect(harness.routeNativeElement!.querySelector('[data-testid="labels-stub"]')).not.toBeNull();
  });

  it('drops ?propose= once the preview opened', async () => {
    const { harness, router } = await render('/rules?propose=news@example.com');
    (harness.routeDebugElement!.componentInstance as RulesPage).clearPropose();
    await harness.fixture.whenStable();
    expect(router.url).toBe('/rules');
  });
});
