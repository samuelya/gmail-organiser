import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { PageHeader } from './page-header';

/** Stand-in for a feature page that a later milestone delivers. Inputs come from route data. */
@Component({
  selector: 'app-placeholder-page',
  imports: [MatCardModule, PageHeader],
  template: `
    <app-page-header [title]="heading()" />
    <mat-card appearance="outlined">
      <mat-card-content>
        <p class="m-0">Coming in {{ milestone() }}.</p>
      </mat-card-content>
    </mat-card>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlaceholderPage {
  readonly heading = input.required<string>();
  readonly milestone = input.required<string>();
}
