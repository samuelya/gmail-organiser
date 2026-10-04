import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, map, of } from 'rxjs';
import { DEFAULT_FLAG_LABELS } from '../review/review.models';
import { SettingsService } from '../settings/settings.service';
import { openFilterPreview } from './filter-preview-dialog.component';
import { FilterDto, FilterProposalDto, FilterRequest } from './rules.models';
import { RulesService } from './rules.service';

/** Filters proposed from approved senders no active filter covers, 20 per page with "Load more". */
@Component({
  selector: 'app-filter-proposals',
  imports: [DecimalPipe, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <section class="flex flex-col gap-2" aria-labelledby="proposals-heading">
      <h2 id="proposals-heading" class="m-0 text-base font-medium">Proposed filters</h2>
      @if (loading() && !items().length) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading proposed filters" />
      }
      @if (loadFailed()) {
        <p class="m-0 flex items-center gap-2" role="alert" data-testid="proposals-error">
          <mat-icon aria-hidden="true">error</mat-icon>
          Proposed filters could not be loaded.
          <button mat-button type="button" (click)="load(nextPage())">Retry</button>
        </p>
      }
      @if (loaded() && total() === 0) {
        <p class="muted m-0" role="status" data-testid="no-proposals">
          No proposals. Approved senders without a covering filter are proposed here.
        </p>
      }
      <ul class="m-0 flex list-none flex-col p-0">
        @for (p of items(); track p.senderAddress) {
          <li class="row flex flex-wrap items-center gap-x-4 gap-y-1 py-2" data-testid="proposal">
            <div class="min-w-48 flex-1">
              <div class="break-all">{{ p.senderAddress }}</div>
              @if (p.displayName) {
                <div class="muted text-sm">{{ p.displayName }}</div>
              }
            </div>
            <span class="text-sm">
              {{ p.messageCount | number }} {{ p.messageCount === 1 ? 'message' : 'messages' }}
            </span>
            <span class="text-sm" data-testid="proposal-pattern">{{ pattern(p) }}</span>
            <button
              mat-stroked-button
              type="button"
              (click)="open(p)"
              [attr.aria-label]="'Preview a filter for ' + p.senderAddress"
              data-testid="proposal-preview"
            >
              Preview
            </button>
          </li>
        }
      </ul>
      @if (items().length < total()) {
        <button
          mat-button
          type="button"
          class="self-start"
          [disabled]="loading()"
          (click)="load(nextPage())"
          data-testid="proposals-more"
        >
          Load more
        </button>
      }
    </section>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .row + .row {
      border-top: 1px solid var(--mat-sys-outline-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FilterProposals implements OnInit {
  private readonly rules = inject(RulesService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);

  /** A sender whose preview opens once the first page is loaded (`?propose=`). */
  readonly propose = input<string>();
  /** A filter was created from the preview dialog. */
  readonly created = output<FilterDto>();
  /** The `propose` sender's dialog was opened; the page drops the query param. */
  readonly proposeHandled = output<void>();

  readonly items = signal<FilterProposalDto[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly loadFailed = signal(false);
  private readonly pagesLoaded = signal(0);
  readonly nextPage = computed(() => this.pagesLoaded() + 1);

  private readonly flags = toSignal(
    inject(SettingsService)
      .getSettings()
      .pipe(
        map((s) => ({ action: s.actionLabelName, delete: s.deleteLabelName })),
        catchError(() => of(DEFAULT_FLAG_LABELS)),
      ),
    { initialValue: DEFAULT_FLAG_LABELS },
  );

  /** After the inputs are set, so `propose` is known when the first page arrives. */
  ngOnInit(): void {
    this.load(1);
  }

  load(page: number): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.rules
      .proposals(page)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.loading.set(false);
          this.loaded.set(true);
          this.pagesLoaded.set(page);
          this.total.set(result.total);
          const known = new Set(this.items().map((p) => p.senderAddress));
          this.items.update((items) => [
            ...(page === 1 ? [] : items),
            ...result.items.filter((p) => page === 1 || !known.has(p.senderAddress)),
          ]);
          if (page === 1) this.openProposed();
        },
        error: () => {
          this.loading.set(false);
          this.loadFailed.set(true);
        },
      });
  }

  /** "Topic · Act" with the settings' flag label names. */
  pattern(p: FilterProposalDto): string {
    const flags = this.flags();
    return [
      p.pattern.topicLabel,
      p.pattern.needsAction ? flags.action : null,
      p.pattern.toBeDeleted ? flags.delete : null,
    ]
      .filter((s): s is string => !!s)
      .join(' · ');
  }

  open(p: FilterProposalDto): void {
    this.openDialog(p.senderAddress, p.suggested);
  }

  private openProposed(): void {
    const address = this.propose()?.trim();
    if (!address) return;
    const match = this.items().find((p) => p.senderAddress.toLowerCase() === address.toLowerCase());
    this.proposeHandled.emit();
    this.openDialog(match?.senderAddress ?? address, match?.suggested ?? null);
  }

  private openDialog(from: string, request: FilterRequest | null): void {
    openFilterPreview(this.dialog, { from, request })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((filter) => {
        if (!filter) return;
        const before = this.items().length;
        this.items.update((items) =>
          items.filter((p) => p.senderAddress.toLowerCase() !== from.toLowerCase()),
        );
        if (this.items().length < before) this.total.update((t) => Math.max(0, t - 1));
        this.created.emit(filter);
      });
  }
}
