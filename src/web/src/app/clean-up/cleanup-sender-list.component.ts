import { DatePipe } from '@angular/common';
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
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { debounceTime, distinctUntilChanged, filter, map } from 'rxjs';
import { PagedDto } from '../core/paging.models';
import { cleanSearch, MAX_SEARCH_LENGTH } from '../senders/senders.models';
import { CleanupSender } from './clean-up.models';

export const CLEANUP_SEARCH_DEBOUNCE_MS = 300;

/** The left pane: search and the paged senders of delete-labelled mail, most messages first. */
@Component({
  selector: 'app-cleanup-sender-list',
  imports: [
    DatePipe,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatListModule,
    MatPaginatorModule,
    MatProgressBarModule,
    ReactiveFormsModule,
  ],
  template: `
    <div class="flex flex-col gap-3">
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
        <p class="muted m-0" data-testid="senders-empty">No senders match.</p>
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
                [attr.aria-label]="s.count + ' messages'"
                data-testid="sender-count"
                >{{ s.count }}</span
              >
            </span>
            <span matListItemLine class="truncate">{{ s.address }}</span>
            <span matListItemLine class="flex items-center gap-2">
              @if (s.newestAt) {
                <span data-testid="sender-newest">{{ s.newestAt | date: 'mediumDate' }}</span>
              }
              @if (s.protectedCount > 0) {
                <span data-testid="sender-protected">· {{ s.protectedCount }} protected</span>
              }
              @if (s.allowlisted) {
                <span class="chip" data-testid="sender-allowlisted">Allowlisted</span>
              }
              @if (s.allowlistedByDomain) {
                <span class="chip" data-testid="sender-allowlisted-domain"
                  >Allowlisted (domain)</span
                >
              }
            </span>
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
    .chip {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      border-radius: 999px;
      padding: 0 0.5rem;
      font: var(--mat-sys-label-small);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CleanupSenderList {
  readonly result = input<PagedDto<CleanupSender> | null>(null);
  readonly selected = input<string | null>(null);
  readonly pageSize = input.required<number>();
  readonly loading = input(false);
  readonly failed = input(false);
  readonly searchChange = output<string>();
  readonly pageChange = output<number>();
  readonly selectSender = output<string>();

  readonly maxSearch = MAX_SEARCH_LENGTH;
  readonly search = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(MAX_SEARCH_LENGTH)],
  });

  constructor() {
    this.search.valueChanges
      .pipe(
        debounceTime(CLEANUP_SEARCH_DEBOUNCE_MS),
        filter(() => this.search.valid),
        map(cleanSearch),
        distinctUntilChanged(),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((value) => this.searchChange.emit(value));
  }

  onPage(event: PageEvent): void {
    this.pageChange.emit(event.pageIndex + 1);
  }
}
