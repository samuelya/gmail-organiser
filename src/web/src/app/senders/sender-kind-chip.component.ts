import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SENDER_KINDS, SenderKind } from './senders.models';

/** Display name per kind; the filter chips use the same names. */
export const SENDER_KIND_LABELS: Readonly<Record<SenderKind, string>> = {
  human: 'Human',
  bulk: 'Bulk',
  mixed: 'Mixed',
  unknown: 'Unknown',
};

/** The kind filter's options, in display order. */
export const SENDER_KIND_OPTIONS = SENDER_KINDS.map((kind) => ({
  kind,
  label: SENDER_KIND_LABELS[kind],
}));

/** A sender's kind as a small coloured badge; the colour comes from the theme, one role per kind. */
@Component({
  selector: 'app-sender-kind-chip',
  template: `<span class="kind" [class]="'kind-' + kind()" data-testid="kind-chip">{{
    label()
  }}</span>`,
  styles: `
    .kind {
      display: inline-block;
      padding: 0 0.5rem;
      border-radius: 9999px;
      font: var(--mat-sys-label-medium);
      line-height: 1.5rem;
      white-space: nowrap;
    }
    .kind-human {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
    .kind-bulk {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }
    .kind-mixed {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .kind-unknown {
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SenderKindChip {
  readonly kind = input.required<SenderKind>();
  readonly label = computed(() => SENDER_KIND_LABELS[this.kind()] ?? this.kind());
}
