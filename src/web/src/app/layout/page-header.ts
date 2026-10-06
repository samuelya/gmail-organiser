import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Page title with an optional one-line description and an actions slot:
 * `<app-page-header title="…" description="…"><button actions>…</button></app-page-header>`.
 */
@Component({
  selector: 'app-page-header',
  template: `
    <header class="mb-4 flex flex-wrap items-center gap-3">
      <div class="min-w-0 flex-1">
        <h1 class="page-title m-0">{{ title() }}</h1>
        @if (description(); as text) {
          <p class="page-description m-0 mt-1" data-testid="page-description">{{ text }}</p>
        }
      </div>
      <div class="flex flex-wrap items-center gap-2"><ng-content select="[actions]" /></div>
    </header>
  `,
  styles: `
    .page-title {
      font: var(--mat-sys-headline-small);
      overflow-wrap: anywhere;
    }
    .page-description {
      font: var(--mat-sys-body-medium);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageHeader {
  readonly title = input.required<string>();
  readonly description = input<string>();
}
