import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { catchError, map, of, startWith, switchMap } from 'rxjs';
import { SenderPolicyDto } from '../policies/policies.models';
import { PoliciesService } from '../policies/policies.service';

/** The search matches substrings, so other senders' policies can come first; the exact one is in this page. */
const SEARCH_PAGE_SIZE = 20;

/** The approved sender-scope policy for `address`, if any, from a page of search hits. */
export function coveringPolicy(
  address: string,
  policies: readonly SenderPolicyDto[],
): SenderPolicyDto | null {
  const key = address.trim().toLowerCase();
  return (
    policies.find(
      (p) => p.status === 'approved' && p.scope === 'sender' && p.scopeKey.toLowerCase() === key,
    ) ?? null
  );
}

/** "Covered by policy" in the Review sender header, linking to the sender's approved policy; nothing without one. */
@Component({
  selector: 'app-policy-coverage-chip',
  imports: [MatIconModule, RouterLink],
  template: `
    @if (policy(); as p) {
      <a
        class="chip inline-flex items-center gap-1 px-2 py-0.5 text-sm"
        [routerLink]="['/policies', p.id]"
        [attr.aria-label]="'Covered by policy: open the policy for ' + p.scopeKey"
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
  private readonly policies = inject(PoliciesService);

  readonly address = input.required<string>();

  /** Cleared while the next sender's lookup is in flight, so the chip never shows for the wrong sender. */
  readonly policy = toSignal(
    toObservable(this.address).pipe(
      switchMap((address) =>
        this.policies
          .list({ status: 'approved', search: address, page: 1, pageSize: SEARCH_PAGE_SIZE })
          .pipe(
            map((page) => coveringPolicy(address, page.items)),
            // No chip is the safe fallback; the header stays usable.
            catchError(() => of(null)),
            startWith(null),
          ),
      ),
    ),
    { initialValue: null },
  );
}
