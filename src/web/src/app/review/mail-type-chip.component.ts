import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { label, MAIL_TYPES } from '../policies/policies.models';

/** Each mail type's hue; the chip mixes it into the theme's surface so it reads in light and dark. */
const MAIL_TYPE_HUES: Readonly<Record<string, string>> = {
  personal: '#1e88e5',
  action_bill: '#e53935',
  receipt: '#43a047',
  statement_document: '#00897b',
  account_alert: '#fb8c00',
  notification: '#8e24aa',
  newsletter: '#3949ab',
  marketing: '#d81b60',
  social: '#00acc1',
  security_otp: '#c0a000',
};

/** The hue of a type the web does not know yet. */
const FALLBACK_HUE = '#757575';

/** A snake_case `MailType` for display: `action_bill` → `action bill`. */
export function mailTypeLabel(type: string): string {
  return label(type);
}

export function mailTypeHue(type: string): string {
  return MAIL_TYPE_HUES[type] ?? FALLBACK_HUE;
}

/** The members' mail type when they all have the same one; null when none has one or they differ. */
export function commonMailType(members: readonly { mailType?: string | null }[]): string | null {
  const types = new Set(members.map((m) => m.mailType ?? null));
  const [only] = types;
  return types.size === 1 ? (only ?? null) : null;
}

export { MAIL_TYPES };

/** The mail type of a suggestion or group card, coloured per type; nothing without one. */
@Component({
  selector: 'app-mail-type-chip',
  imports: [MatTooltipModule],
  template: `
    @if (type(); as t) {
      <span
        class="mail-type"
        [style.--mail-type-hue]="hue()"
        [attr.data-type]="t"
        [matTooltip]="'Mail type: ' + text()"
        data-testid="mail-type-chip"
        >{{ text() }}</span
      >
    }
  `,
  styles: `
    .mail-type {
      background: color-mix(in srgb, var(--mail-type-hue) 22%, var(--mat-sys-surface));
      border: 1px solid color-mix(in srgb, var(--mail-type-hue) 55%, var(--mat-sys-surface));
      color: var(--mat-sys-on-surface);
      border-radius: 0.5rem;
      padding: 0.125rem 0.5rem;
      white-space: nowrap;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MailTypeChip {
  readonly type = input<string | null | undefined>(null);
  readonly text = computed(() => mailTypeLabel(this.type() ?? ''));
  readonly hue = computed(() => mailTypeHue(this.type() ?? ''));
}
