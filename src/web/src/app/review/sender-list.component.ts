import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { debounceTime, distinctUntilChanged, filter, map } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { cleanSearch, MAX_SEARCH_LENGTH } from '../senders/senders.models';
import { REVIEW_STATUSES, ReviewSenderDto, ReviewStatus } from './review.models';

export const REVIEW_SEARCH_DEBOUNCE_MS = 300;

/** The left pane: status filter, search and the paged senders with suggestions in that status. */
@Component({
  selector: 'app-review-sender-list',
  imports: [
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatListModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    ReactiveFormsModule,
  ],
  template: `
    <div class="flex flex-col gap-3">
      <mat-button-toggle-group
        [value]="status()"
        (change)="statusChange.emit($event.value)"
        aria-label="Suggestion status"
        hideSingleSelectionIndicator
        data-testid="status-filter"
      >
        @for (s of statuses; track s.value) {
          <mat-button-toggle [value]="s.value">{{ s.label }}</mat-button-toggle>
        }
      </mat-button-toggle-group>
      @if (reanalysed() || (alternatives() ?? 0) > 0) {
        <mat-slide-toggle
          [checked]="reanalysed()"
          (change)="reanalysedChange.emit($event.checked)"
          data-testid="filter-reanalysed"
          >Re-analysed ({{ alternatives() ?? 0 }})</mat-slide-toggle
        >
      }
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Search senders</mat-label>
        <mat-icon matPrefix aria-hidden="true">search</mat-icon>
        <input matInput type="search" [formControl]="search" data-testid="sender-search" />
        @if (search.hasError('maxlength')) {
          <mat-error>At most {{ maxSearch }} characters.</mat-error>
        }
      </mat-form-field>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading senders" />
      }
      @if (failed()) {
        <p class="muted m-0" data-testid="senders-failed">The senders could not be loaded.</p>
      } @else if (result()?.items?.length === 0) {
        <p class="muted m-0" data-testid="senders-empty">
          No senders with {{ status() }} suggestions.
        </p>
      }
      <mat-action-list aria-label="Senders" data-testid="sender-list">
        @for (s of result()?.items ?? []; track s.address) {
          <button
            mat-list-item
            type="button"
            [activated]="s.address === selected()"
            [attr.aria-current]="s.address === selected() ? 'true' : null"
            (click)="selectSender.emit(s.address)"
            data-testid="sender-item"
          >
            <span matListItemTitle class="flex items-center gap-2">
              <span class="min-w-0 flex-1 truncate">{{ s.displayName || s.address }}</span>
              <span
                class="count"
                [attr.aria-label]="countOf(s) + ' ' + status()"
                data-testid="sender-count"
                >{{ countOf(s) }}</span
              >
            </span>
            <span matListItemLine class="truncate">{{ s.address }}</span>
          </button>
        }
      </mat-action-list>
      @if ((result()?.total ?? 0) > pageSize()) {
        <mat-paginator
          [length]="result()?.total ?? 0"
          [pageIndex]="(result()?.page ?? 1) - 1"
          [pageSize]="pageSize()"
          hidePageSize
          (page)="onPage($event)"
          aria-label="Senders pages"
        />
      }
    </div>
  `,
  styles: `
    .muted {
      color: var(--mat-sys-on-surface-variant);
    }
    .count {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
      border-radius: 999px;
      padding: 0 0.5rem;
      font: var(--mat-sys-label-medium);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SenderList {
  readonly result = input<PagedDto<ReviewSenderDto> | null>(null);
  readonly status = input.required<ReviewStatus>();
  readonly selected = input<string | null>(null);
  readonly pageSize = input.required<number>();
  readonly loading = input(false);
  readonly failed = input(false);
  /** The "Re-analysed" filter is on: only suggestions with a re-analysis result waiting. */
  readonly reanalysed = input(false);
  /** How many suggestions have one, from the summary. */
  readonly alternatives = input<number | null>(null);
  readonly reanalysedChange = output<boolean>();
  readonly statusChange = output<ReviewStatus>();
  readonly searchChange = output<string>();
  readonly pageChange = output<number>();
  readonly selectSender = output<string>();

  readonly statuses = REVIEW_STATUSES;
  readonly maxSearch = MAX_SEARCH_LENGTH;
  readonly search = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(MAX_SEARCH_LENGTH)],
  });

  constructor() {
    this.search.valueChanges
      .pipe(
        debounceTime(REVIEW_SEARCH_DEBOUNCE_MS),
        filter(() => this.search.valid),
        map(cleanSearch),
        distinctUntilChanged(),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((value) => this.searchChange.emit(value));
  }

  /** The badge counts the suggestions in the filtered status. */
  countOf(sender: ReviewSenderDto): number {
    return sender[this.status()];
  }

  onPage(event: PageEvent): void {
    this.pageChange.emit(event.pageIndex + 1);
  }
}
