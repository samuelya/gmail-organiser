import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

/**
 * "Covered by policy" in the Review sender header, linking to the approved policy the API found for the sender
 * (by its relay-decoded address, then List-Id, then domain); nothing without one.
 */
@Component({
  selector: 'app-policy-coverage-chip',
  imports: [MatIconModule, RouterLink],
  template: `
    @if (policyId(); as id) {
      <a
        class="chip inline-flex items-center gap-1 px-2 py-0.5 text-sm"
        [routerLink]="['/policies', id]"
        aria-label="Covered by policy: open the policy"
        data-testid="policy-coverage"
      >
        <mat-icon class="icon" aria-hidden="true">policy</mat-icon>
        Covered by policy
      </a>
    }
  `,
  styles: `
    .chip {
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0.5rem;
      color: var(--mat-sys-on-surface);
      text-decoration: none;
    }
    .chip:hover {
      background: var(--mat-sys-surface-container-high);
    }
    .icon {
      font-size: 1rem;
      width: 1rem;
      height: 1rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PolicyCoverageChip {
  readonly policyId = input.required<string | null>();
}
