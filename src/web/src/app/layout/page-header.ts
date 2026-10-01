import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Page title with an optional actions slot: `<app-page-header title="…"><button actions>…</button></app-page-header>`. */
@Component({
  selector: 'app-page-header',
  template: `
    <header class="mb-4 flex flex-wrap items-center gap-3">
      <h1 class="page-title m-0 min-w-0 flex-1">{{ title() }}</h1>
      <div class="flex flex-wrap items-center gap-2"><ng-content select="[actions]" /></div>
    </header>
  `,
  styles: `
    .page-title {
      font: var(--mat-sys-headline-small);
      overflow-wrap: anywhere;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageHeader {
  readonly title = input.required<string>();
}
