import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SenderCategoryMix } from './senders.models';

/** Display name per Gmail category tab, in bar order. */
const CATEGORIES: readonly { key: keyof SenderCategoryMix; label: string }[] = [
  { key: 'primary', label: 'Primary' },
  { key: 'promotions', label: 'Promotions' },
  { key: 'social', label: 'Social' },
  { key: 'updates', label: 'Updates' },
  { key: 'forums', label: 'Forums' },
];

/** A sender's messages per category tab as one stacked bar; the colours come from the theme. */
@Component({
  selector: 'app-category-mix-bar',
  imports: [MatTooltipModule],
  template: `
    @if (segments().length) {
      <div
        class="bar"
        role="img"
        tabindex="0"
        [attr.aria-label]="summary()"
        [matTooltip]="summary()"
        data-testid="category-mix"
      >
        @for (s of segments(); track s.key) {
          <span [class]="'seg seg-' + s.key" [style.width.%]="s.percent"></span>
        }
      </div>
    } @else {
      <span class="muted" data-testid="category-mix-none">—</span>
    }
  `,
  styles: `
    .bar {
      display: flex;
      width: 6rem;
      height: 0.5rem;
      border-radius: 9999px;
      overflow: hidden;
      background: var(--mat-sys-surface-container-highest);
    }
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .seg-primary {
      background: var(--mat-sys-primary);
    }
    .seg-promotions {
      background: var(--mat-sys-tertiary);
    }
    .seg-social {
      background: var(--mat-sys-secondary);
    }
    .seg-updates {
      background: var(--mat-sys-outline);
    }
    .seg-forums {
      background: var(--mat-sys-error);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CategoryMixBar {
  readonly mix = input.required<SenderCategoryMix>();

  private readonly total = computed(() =>
    CATEGORIES.reduce((sum, c) => sum + (this.mix()[c.key] ?? 0), 0),
  );

  /** Non-empty categories with their share of the categorised messages. */
  readonly segments = computed(() => {
    const total = this.total();
    if (total <= 0) return [];
    return CATEGORIES.filter((c) => this.mix()[c.key] > 0).map((c) => ({
      ...c,
      count: this.mix()[c.key],
      percent: (this.mix()[c.key] / total) * 100,
    }));
  });

  readonly summary = computed(() =>
    this.segments()
      .map((s) => `${s.label} ${Math.round(s.percent)}%`)
      .join(', '),
  );
}
