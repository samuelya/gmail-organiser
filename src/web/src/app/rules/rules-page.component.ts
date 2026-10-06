import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatTabChangeEvent, MatTabsModule } from '@angular/material/tabs';
import { ActivatedRoute, Router } from '@angular/router';
import { PageHeader } from '../layout/page-header';
import { FiltersTab } from './filters-tab.component';
import { FindingsTab } from './findings-tab.component';
import { LabelsTab } from './labels-tab.component';
import { RULES_TABS } from './rules.models';

/** `/rules`: Filters, Findings and Labels tabs; the active tab is `?tab=`, `?propose=<address>` opens a filter preview. */
@Component({
  selector: 'app-rules-page',
  imports: [FiltersTab, FindingsTab, LabelsTab, MatCardModule, MatTabsModule, PageHeader],
  template: `
    <app-page-header
      title="Rules"
      description="Your Gmail filters and labels: what exists, what overlaps, and filters proposed from your policies."
    />
    <mat-card appearance="outlined">
      <mat-card-content>
        <mat-tab-group
          [selectedIndex]="index()"
          (selectedTabChange)="select($event)"
          mat-stretch-tabs="false"
          data-testid="rules-tabs"
        >
          <mat-tab label="Filters">
            <app-filters-tab [propose]="propose()" (proposeHandled)="clearPropose()" />
          </mat-tab>
          <mat-tab label="Findings">
            <app-findings-tab />
          </mat-tab>
          <mat-tab label="Labels">
            <ng-template matTabContent>
              <app-labels-tab />
            </ng-template>
          </mat-tab>
        </mat-tab-group>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RulesPage {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** `?tab=`; unknown values show Filters. */
  readonly tab = input<string>();
  /** `?propose=<address>`. */
  readonly propose = input<string>();

  readonly index = computed(() =>
    Math.max(0, RULES_TABS.indexOf(this.tab() as (typeof RULES_TABS)[number])),
  );

  select(event: MatTabChangeEvent): void {
    if (event.index === this.index()) return;
    this.navigate({ tab: RULES_TABS[event.index] });
  }

  /** The preview was opened; a reload must not open it again. */
  clearPropose(): void {
    this.navigate({ propose: null });
  }

  private navigate(queryParams: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
