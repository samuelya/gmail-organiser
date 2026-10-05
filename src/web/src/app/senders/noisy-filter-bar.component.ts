import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { debounceTime, filter, map } from 'rxjs';
import {
  cleanSearch,
  MAX_DORMANT_DAYS,
  MAX_MIN_MESSAGES,
  MAX_SEARCH_LENGTH,
  NoisyQuery,
  searchValidator,
} from './senders.models';

export const FILTER_DEBOUNCE_MS = 300;

/** The filters the bar edits; paging is the page's. */
export type NoisyFilters = Pick<
  NoisyQuery,
  'minMessages' | 'minUnreadPercent' | 'dormantDays' | 'search'
>;

/** The noisy-senders filters: emits the valid values after a pause in typing. */
@Component({
  selector: 'app-noisy-filter-bar',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    ReactiveFormsModule,
  ],
  template: `
    <form
      class="flex flex-wrap items-start gap-x-4 gap-y-2"
      [formGroup]="form"
      (ngSubmit)="apply()"
    >
      <mat-form-field class="w-36" subscriptSizing="dynamic">
        <mat-label>Min messages</mat-label>
        <input
          matInput
          type="number"
          min="1"
          [max]="maxMessages"
          formControlName="minMessages"
          data-testid="min-messages"
        />
        @if (form.controls.minMessages.invalid) {
          <mat-error>1 to {{ maxMessages }}.</mat-error>
        }
      </mat-form-field>
      <mat-form-field class="w-36" subscriptSizing="dynamic">
        <mat-label>Min unread %</mat-label>
        <input
          matInput
          type="number"
          min="0"
          max="100"
          formControlName="minUnreadPercent"
          data-testid="min-unread"
        />
        @if (form.controls.minUnreadPercent.invalid) {
          <mat-error>0 to 100.</mat-error>
        }
      </mat-form-field>
      <mat-form-field class="w-40" subscriptSizing="dynamic">
        <mat-label>Dormant days</mat-label>
        <input
          matInput
          type="number"
          min="1"
          [max]="maxDormant"
          formControlName="dormantDays"
          placeholder="Off"
          data-testid="dormant-days"
        />
        <mat-hint>Last seen longer ago; empty for all</mat-hint>
        @if (form.controls.dormantDays.invalid) {
          <mat-error>Empty, or 1 to {{ maxDormant }}.</mat-error>
        }
      </mat-form-field>
      <mat-form-field class="min-w-0 flex-1 basis-56" subscriptSizing="dynamic">
        <mat-label>Search senders</mat-label>
        <mat-icon matPrefix aria-hidden="true">search</mat-icon>
        <input matInput type="search" formControlName="search" data-testid="noisy-search" />
        @if (form.controls.search.hasError('controlChars')) {
          <mat-error>Remove tabs and line breaks.</mat-error>
        } @else if (form.controls.search.hasError('maxlength')) {
          <mat-error>At most {{ maxSearch }} characters.</mat-error>
        }
      </mat-form-field>
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NoisyFilterBar {
  private readonly destroyRef = inject(DestroyRef);

  readonly query = input.required<NoisyQuery>();
  readonly changed = output<NoisyFilters>();

  readonly maxMessages = MAX_MIN_MESSAGES;
  readonly maxDormant = MAX_DORMANT_DAYS;
  readonly maxSearch = MAX_SEARCH_LENGTH;

  readonly form = new FormGroup({
    minMessages: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(1),
      Validators.max(MAX_MIN_MESSAGES),
      integer,
    ]),
    minUnreadPercent: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(0),
      Validators.max(100),
      integer,
    ]),
    dormantDays: new FormControl<number | null>(null, [
      Validators.min(1),
      Validators.max(MAX_DORMANT_DAYS),
      integer,
    ]),
    search: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MAX_SEARCH_LENGTH), searchValidator],
    }),
  });

  constructor() {
    // The URL is the source of truth: back/forward or a link shows its values here.
    effect(() => {
      const q = this.query();
      this.form.setValue(
        {
          minMessages: q.minMessages,
          minUnreadPercent: q.minUnreadPercent,
          dormantDays: q.dormantDays,
          search: q.search,
        },
        { emitEvent: false },
      );
    });
    this.form.valueChanges
      .pipe(
        debounceTime(FILTER_DEBOUNCE_MS),
        filter(() => this.form.valid),
        map(() => this.filters()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((filters) => this.changed.emit(filters));
  }

  /** Enter applies at once. */
  apply(): void {
    if (this.form.valid) this.changed.emit(this.filters());
  }

  private filters(): NoisyFilters {
    const v = this.form.getRawValue();
    return {
      minMessages: v.minMessages!,
      minUnreadPercent: v.minUnreadPercent!,
      dormantDays: v.dormantDays ?? null,
      search: cleanSearch(v.search),
    };
  }
}

function integer(control: AbstractControl<number | null>): ValidationErrors | null {
  return control.value === null || Number.isInteger(control.value) ? null : { integer: true };
}
