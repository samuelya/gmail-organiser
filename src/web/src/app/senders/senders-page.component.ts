import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, debounceTime, filter, map, of, Subject, switchMap } from 'rxjs';
import { openConfirm } from '../core/confirm-dialog';
import { isActiveJob, JobDto, newerJob } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { PagedDto } from '../core/paging.models';
import { PageHeader } from '../layout/page-header';
import { SenderProgress } from './sender-progress.component';
import {
  analysedPercent,
  DEFAULT_SENDER_QUERY,
  MAX_SEARCH_LENGTH,
  normaliseFetchTarget,
  PAGE_SIZES,
  parseSenderQuery,
  relativeTime,
  SENDER_FETCH_JOB,
  SenderDto,
  SenderQuery,
  senderQueryParams,
  SenderSort,
} from './senders.models';
import { SendersService } from './senders.service';

export const SEARCH_DEBOUNCE_MS = 300;

/** `/senders`: the paged senders list with per-sender progress and "Fetch all from sender". */
@Component({
  selector: 'app-senders-page',
  imports: [
    DatePipe,
    DecimalPipe,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatSortModule,
    MatTableModule,
    MatTooltipModule,
    PageHeader,
    ReactiveFormsModule,
    RouterLink,
    SenderProgress,
  ],
  templateUrl: './senders-page.component.html',
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .address {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .analysed-bar {
      width: 6rem;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SendersPage {
  private readonly senders = inject(SendersService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly jobs = inject(JobsService);
  private readonly requests = new Subject<SenderQuery>();

  readonly columns = ['sender', 'domain', 'total', 'analysed', 'lastSeen', 'actions'];
  readonly pageSizes = PAGE_SIZES;
  /** The URL is the source of truth for search, page, size and sort. */
  readonly query = toSignal(this.route.queryParamMap.pipe(map(parseSenderQuery)), {
    initialValue: DEFAULT_SENDER_QUERY,
  });
  readonly result = signal<PagedDto<SenderDto> | null>(null);
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  /** Addresses or domains whose fetch request is in flight. */
  readonly starting = signal<ReadonlySet<string>>(new Set());
  /** Job ids whose cancel request is in flight. */
  readonly cancelling = signal<ReadonlySet<string>>(new Set());

  readonly search = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(MAX_SEARCH_LENGTH)],
  });
  readonly targetForm = new FormGroup({
    target: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, fetchTargetValidator],
    }),
  });
  readonly target = this.targetForm.controls.target;

  /** Each row with the live state of its active fetch job (`null` once it finished). */
  readonly rows = computed(() =>
    (this.result()?.items ?? []).map((sender) => {
      const held = sender.activeFetchJob;
      const job = held ? newerJob(held, this.jobs.job(held.id)) : null;
      return {
        sender,
        job: job && isActiveJob(job) ? job : null,
        percent: analysedPercent(sender),
      };
    }),
  );

  /** Changes when a sender fetch finishes or the hub reconnects: counts and active jobs moved. */
  private readonly refreshKey = computed(() => {
    const finished = this.jobs
      .jobs()
      .filter((j) => j.type === SENDER_FETCH_JOB && !isActiveJob(j))
      .map((j) => `${j.id}:${j.status}`)
      .sort()
      .join(',');
    return `${finished}|${this.jobs.reconnects()}`;
  });

  constructor() {
    this.requests
      .pipe(
        switchMap((query) =>
          this.senders.list(query).pipe(
            map((page) => page as PagedDto<SenderDto> | null),
            catchError(() => of(null)),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        this.loading.set(false);
        this.loadFailed.set(!page);
        if (page) this.result.set(page);
      });
    effect(() => {
      const query = this.query();
      this.refreshKey();
      untracked(() => this.load(query));
    });
    // Back/forward or a link changes the search: show it in the box without searching again.
    effect(() => {
      const search = this.query().search;
      untracked(() => {
        if (this.search.value.trim() !== search) this.search.setValue(search, { emitEvent: false });
      });
    });
    this.search.valueChanges
      .pipe(
        debounceTime(SEARCH_DEBOUNCE_MS),
        filter(() => this.search.valid),
        map((value) => value.trim()),
        filter((search) => search !== this.query().search),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((search) => this.navigate({ search, page: 1 }));
  }

  /** Reloads the current page. */
  reload(): void {
    this.load(this.query());
  }

  onPage(event: PageEvent): void {
    const pageSize = event.pageSize;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ page, pageSize });
  }

  onSort(sort: Sort): void {
    const d = DEFAULT_SENDER_QUERY;
    const cleared = !sort.direction;
    this.navigate({
      sort: cleared ? d.sort : (sort.active as SenderSort),
      dir: cleared ? d.dir : sort.direction || d.dir,
      page: 1,
    });
  }

  clearSearch(): void {
    this.search.setValue('', { emitEvent: false });
    this.navigate({ search: '', page: 1 });
  }

  fetchSender(sender: SenderDto): void {
    this.startFetch(sender.address);
  }

  submitTarget(): void {
    const target = normaliseFetchTarget(this.target.value);
    if (!target) {
      this.target.markAsTouched();
      return;
    }
    this.startFetch(target, () => this.targetForm.reset());
  }

  cancel(job: JobDto, label: string): void {
    openConfirm(this.dialog, {
      title: 'Cancel sender fetch?',
      message: `Stops fetching from ${label} at its next checkpoint. Mail already fetched stays.`,
      confirm: 'Cancel fetch',
    })
      .pipe(
        filter((confirmed) => confirmed),
        switchMap(() => {
          this.cancelling.update((ids) => new Set(ids).add(job.id));
          return this.jobs.cancel(job.id).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => this.cancelling.update((ids) => without(ids, job.id)));
  }

  isStarting(target: string): boolean {
    return this.starting().has(target);
  }

  lastSeen(iso: string): string {
    return relativeTime(iso);
  }

  private startFetch(target: string, done?: () => void): void {
    if (this.isStarting(target)) return;
    this.starting.update((targets) => new Set(targets).add(target));
    this.senders
      .fetchFromSender(target)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.starting.update((targets) => without(targets, target));
          done?.();
          this.snackBar.open(
            `Fetching all mail from ${target}. It runs after any fetch already in progress.`,
            'Dismiss',
            { duration: 6000 },
          );
          this.reload();
        },
        // The error interceptor shows the server's problem detail.
        error: () => this.starting.update((targets) => without(targets, target)),
      });
  }

  private load(query: SenderQuery): void {
    this.loading.set(true);
    this.requests.next(query);
  }

  private navigate(patch: Partial<SenderQuery>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: senderQueryParams({ ...this.query(), ...patch }),
    });
  }
}

/** API-side rules for a sender fetch target; empty is left to `required`. */
function fetchTargetValidator(control: AbstractControl<string>) {
  return !control.value.trim() || normaliseFetchTarget(control.value) ? null : { target: true };
}

function without<T>(set: ReadonlySet<T>, value: T): ReadonlySet<T> {
  const next = new Set(set);
  next.delete(value);
  return next;
}
