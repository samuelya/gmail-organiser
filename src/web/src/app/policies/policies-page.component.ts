import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, debounceTime, map, Observable, of, Subject, switchMap } from 'rxjs';
import { errorMessage } from '../core/error.interceptor';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import {
  cleanSearch,
  DEFAULT_POLICY_QUERY,
  MAX_SEARCH_LENGTH,
  POLICY_STATUSES,
  PolicyQuery,
  policyQueryParams,
  PolicyStatus,
  parsePolicyQuery,
  SenderPolicyDto,
} from './policies.models';
import { PoliciesService } from './policies.service';
import { PolicyList } from './policy-list.component';

export const SEARCH_DEBOUNCE_MS = 300;

const TAB_LABELS: Readonly<Record<PolicyStatus, string>> = {
  proposed: 'Proposed',
  approved: 'Approved',
  rejected: 'Rejected',
};

/** `/policies`: sender policies by status; the URL holds the tab, search and page. */
@Component({
  selector: 'app-policies-page',
  imports: [
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatTabsModule,
    PageHeader,
    PolicyList,
    ReactiveFormsModule,
  ],
  template: `
    <app-page-header title="Policies" />
    <mat-card appearance="outlined">
      <mat-card-content class="flex flex-col gap-4">
        <p class="muted m-0">
          A policy decides all mail from one sender, domain or list. Approving it applies it to past
          mail (undo from History) and to new mail as it arrives.
        </p>
        <mat-tab-group
          [selectedIndex]="statuses.indexOf(query().status)"
          (selectedIndexChange)="onTab($event)"
          mat-stretch-tabs="false"
          aria-label="Policy status"
        >
          @for (status of statuses; track status) {
            <mat-tab [label]="tabLabel(status)" />
          }
        </mat-tab-group>
        <mat-form-field class="w-full max-w-md" subscriptSizing="dynamic">
          <mat-label>Search senders</mat-label>
          <mat-icon matPrefix aria-hidden="true">search</mat-icon>
          <input matInput [formControl]="search" [maxlength]="maxSearch" data-testid="search" />
        </mat-form-field>
        @if (loading()) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading policies" />
        }
        @if (loadError(); as error) {
          <p class="m-0 flex items-center gap-2" role="alert" data-testid="load-error">
            <mat-icon aria-hidden="true">error</mat-icon>
            <span class="flex-1">{{ error }}</span>
            <button mat-button type="button" (click)="reload()">Retry</button>
          </p>
        }
        @if (result(); as page) {
          @if (page.total === 0) {
            <p class="muted m-0" role="status" data-testid="empty">
              No {{ query().status }} policies{{
                query().search ? ' matching “' + query().search + '”' : ''
              }}.
            </p>
          } @else {
            <app-policy-list [page]="page" (paged)="onPage($event)" />
          }
        }
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
export class PoliciesPage {
  private readonly policies = inject(PoliciesService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly jobs = inject(JobsService);
  private readonly requests = new Subject<PolicyQuery>();
  private readonly countRequests = new Subject<string>();

  readonly statuses = POLICY_STATUSES;
  readonly maxSearch = MAX_SEARCH_LENGTH;
  /** The URL is the source of truth for the tab, search and page. */
  readonly query = toSignal(this.route.queryParamMap.pipe(map(parsePolicyQuery)), {
    initialValue: DEFAULT_POLICY_QUERY,
  });
  readonly result = signal<PagedDto<SenderPolicyDto> | null>(null);
  readonly counts = signal<Record<PolicyStatus, number> | null>(null);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly search = new FormControl('', { nonNullable: true });

  constructor() {
    this.requests
      .pipe(
        switchMap((query) =>
          this.policies.list(query).pipe(
            map((page): PagedDto<SenderPolicyDto> | string => page),
            catchError((error: unknown) => of(problemText(error))),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.loading.set(false);
        if (typeof page === 'string') {
          this.loadError.set(page);
          return;
        }
        this.loadError.set(null);
        this.result.set(page);
      });
    this.countRequests
      .pipe(
        switchMap((search) =>
          this.policies.counts(search).pipe(catchError((): Observable<null> => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((counts) => this.counts.set(counts));
    this.search.valueChanges
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), map(cleanSearch), takeUntilDestroyed(this.destroyRef))
      .subscribe((search) => {
        if (search !== this.query().search) this.navigate({ search, page: 1 });
      });
    effect(() => {
      const query = this.query();
      this.jobs.reconnects();
      untracked(() => {
        if (this.search.value.trim() !== query.search)
          this.search.setValue(query.search, { emitEvent: false });
        this.load(query);
      });
    });
  }

  tabLabel(status: PolicyStatus): string {
    const count = this.counts()?.[status];
    return count === undefined ? TAB_LABELS[status] : `${TAB_LABELS[status]} (${count})`;
  }

  reload(): void {
    this.load(this.query());
  }

  /** Also fires when Back/Forward moves the bound index; then the URL already has the tab. */
  onTab(index: number): void {
    if (this.statuses[index] === this.query().status) return;
    this.navigate({ status: this.statuses[index], page: 1 });
  }

  onPage(event: PageEvent): void {
    const pageSize = event.pageSize;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ page, pageSize });
  }

  private load(query: PolicyQuery): void {
    this.loading.set(true);
    this.requests.next(query);
    this.countRequests.next(query.search);
  }

  private navigate(change: Partial<PolicyQuery>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: policyQueryParams({ ...this.query(), ...change }),
    });
  }
}

/** A list error: the ProblemDetails title and detail. */
function problemText(error: unknown): string {
  return error instanceof HttpErrorResponse ? errorMessage(error) : 'Something went wrong.';
}
