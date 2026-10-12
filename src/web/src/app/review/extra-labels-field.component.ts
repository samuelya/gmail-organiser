import { COMMA, ENTER } from '@angular/cdk/keycodes';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  forwardRef,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  ControlValueAccessor,
  FormControl,
  NG_VALIDATORS,
  NG_VALUE_ACCESSOR,
  ReactiveFormsModule,
  ValidationErrors,
  Validator,
} from '@angular/forms';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { LabelDto } from './labels.models';
import { labelPathError } from './labels.service';
import { MAX_EXTRA_LABELS } from './review.models';

/** Autocomplete shows at most this many matching labels. */
const MAX_OPTIONS = 50;

/** Whether two label lists hold the same names, ignoring case and order. */
export function sameLabels(a: readonly string[], b: readonly string[]): boolean {
  const key = (list: readonly string[]) => [...new Set(list.map((l) => l.toLowerCase()))].sort();
  return key(a).join('\n') === key(b).join('\n');
}

/**
 * "Additional labels" (#524): a chip per label, picked from the label list or typed as a new path and
 * added on Enter or comma. A form control (value `string[]`) that is invalid while a chip equals an
 * `exclude`d name, there are more than `max`, or typed text has not been added.
 */
@Component({
  selector: 'app-extra-labels-field',
  imports: [
    MatAutocompleteModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    ReactiveFormsModule,
  ],
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => ExtraLabelsField), multi: true },
    { provide: NG_VALIDATORS, useExisting: forwardRef(() => ExtraLabelsField), multi: true },
  ],
  template: `
    <mat-form-field class="w-full" subscriptSizing="dynamic">
      <mat-label>Additional labels</mat-label>
      <mat-chip-grid
        #grid
        [formControl]="chips"
        aria-label="Additional labels"
        data-testid="extra-labels-grid"
      >
        @for (label of value(); track label) {
          <mat-chip-row
            (removed)="remove(label)"
            [disabled]="disabled()"
            data-testid="extra-label-chip"
          >
            {{ label }}
            @if (isNew(label)) {
              <span class="new" data-testid="extra-label-new">new</span>
            }
            <button
              matChipRemove
              [attr.aria-label]="'Remove ' + label"
              data-testid="extra-label-remove"
            >
              <mat-icon aria-hidden="true">cancel</mat-icon>
            </button>
          </mat-chip-row>
        }
        <input
          #input
          [matChipInputFor]="grid"
          [matChipInputSeparatorKeyCodes]="separators"
          (matChipInputTokenEnd)="add($event.value)"
          [matAutocomplete]="auto"
          (input)="type(input.value)"
          (blur)="touched()"
          [disabled]="disabled()"
          [readOnly]="full() && !text()"
          [matAutocompleteDisabled]="full()"
          data-testid="extra-labels-input"
        />
      </mat-chip-grid>
      <mat-autocomplete #auto="matAutocomplete" (optionSelected)="pick($event)">
        @for (option of options(); track option) {
          <mat-option [value]="option" data-testid="extra-label-option">{{ option }}</mat-option>
        }
      </mat-autocomplete>
      @if (error(); as e) {
        <mat-error data-testid="extra-labels-error">{{ e }}</mat-error>
      } @else if (full()) {
        <mat-hint data-testid="extra-labels-full"
          >At most {{ max() }} additional labels; remove one to add another.</mat-hint
        >
      } @else {
        <mat-hint>Pick a label or type a new path; Enter or comma adds it.</mat-hint>
      }
    </mat-form-field>
  `,
  styles: `
    .new {
      margin-left: 0.25rem;
      font-weight: 600;
      color: var(--mat-sys-primary);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExtraLabelsField implements ControlValueAccessor, Validator {
  /** The labels autocomplete offers (user labels only); null while loading. */
  readonly labels = input<readonly LabelDto[] | null>(null);
  /** Names a chip must not be, compared case-insensitively: the topic, document-type and flag labels. */
  readonly exclude = input<readonly (string | null | undefined)[]>([]);
  readonly max = input(MAX_EXTRA_LABELS);
  /** A message from the server for the list, such as a 400 under `extraLabels`. */
  readonly serverError = input<string | null>(null);

  readonly separators = [ENTER, COMMA] as const;
  readonly value = signal<string[]>([]);
  readonly disabled = signal(false);
  /** The typed text; at the limit the input stays editable until it is cleared. */
  protected readonly text = signal('');
  /** Why the typed text was not added; typing again clears it. */
  private readonly inputError = signal<string | null>(null);
  /** The input lost focus with text not yet added. */
  private readonly left = signal(false);
  /** Typed text not added yet keeps the control invalid, so Save cannot drop it. */
  private readonly pending = computed(() => {
    const text = this.text().trim();
    return text ? `Press Enter to add '${text}', or clear it.` : null;
  });

  private readonly names = computed(() =>
    (this.labels() ?? []).filter((l) => l.type === 'user').map((l) => l.name),
  );
  private readonly excluded = computed(
    () => new Set(this.exclude().flatMap((n) => (n?.trim() ? [n.trim().toLowerCase()] : []))),
  );
  /** Why the current chips are invalid: one equals an excluded name, or there are too many. */
  readonly conflict = computed(() => {
    const clash = this.value().find((l) => this.excluded().has(l.toLowerCase()));
    if (clash) return `'${clash}' is already the topic, document type or a flag label.`;
    return this.value().length > this.max() ? `At most ${this.max()} additional labels.` : null;
  });
  readonly error = computed(
    () =>
      this.inputError() ??
      this.conflict() ??
      (this.left() ? this.pending() : null) ??
      this.serverError(),
  );
  readonly full = computed(() => this.value().length >= this.max());
  /** Labels not yet chosen nor excluded that contain the typed text. */
  readonly options = computed(() => {
    const text = this.text().trim().toLowerCase();
    const chosen = new Set(this.value().map((l) => l.toLowerCase()));
    const excluded = this.excluded();
    return this.names()
      .filter((n) => {
        const key = n.toLowerCase();
        return key.includes(text) && !chosen.has(key) && !excluded.has(key);
      })
      .slice(0, MAX_OPTIONS);
  });

  /** Bound to the chip grid so `mat-error` shows; invalid while there is a message. */
  readonly chips = new FormControl<string[]>([], {
    nonNullable: true,
    validators: () => {
      const message = this.error();
      return message ? { extraLabels: message } : null;
    },
  });

  private readonly input = viewChild.required<ElementRef<HTMLInputElement>>('input');
  private readonly trigger = viewChild.required(MatAutocompleteTrigger);
  private onChange: (value: string[]) => void = () => undefined;
  private onTouched: () => void = () => undefined;
  private onValidatorChange: () => void = () => undefined;

  constructor() {
    effect(() => {
      const message = this.error();
      untracked(() => {
        this.chips.updateValueAndValidity({ emitEvent: false });
        if (message) this.chips.markAsTouched();
      });
    });
    effect(() => {
      this.conflict();
      this.pending();
      untracked(() => this.onValidatorChange());
    });
  }

  writeValue(value: readonly string[] | null): void {
    this.value.set([...(value ?? [])]);
    this.chips.setValue(this.value(), { emitEvent: false });
  }

  registerOnChange(fn: (value: string[]) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(disabled: boolean): void {
    this.disabled.set(disabled);
  }

  validate(): ValidationErrors | null {
    const message = this.conflict() ?? this.pending();
    return message ? { extraLabels: message } : null;
  }

  registerOnValidatorChange(fn: () => void): void {
    this.onValidatorChange = fn;
  }

  isNew(label: string): boolean {
    const key = label.toLowerCase();
    return this.labels() !== null && !this.names().some((n) => n.toLowerCase() === key);
  }

  /** Enter or comma: adds the typed path, or keeps it with a message; the highlighted option wins. */
  add(text: string): void {
    const trigger = this.trigger();
    if (trigger.panelOpen && trigger.activeOption) {
      // Comma would otherwise be swallowed; on Enter the panel closes so the trigger does not pick twice.
      trigger.activeOption._selectViaInteraction();
      return;
    }
    const label = text.trim();
    if (!label) return;
    const known = this.names().find((n) => n.toLowerCase() === label.toLowerCase()) ?? label;
    const error = this.rejection(known);
    if (error) {
      this.inputError.set(error);
      return;
    }
    this.commit([...this.value(), known]);
    this.clearInput();
  }

  pick(event: MatAutocompleteSelectedEvent): void {
    const label = String(event.option.value);
    this.clearInput();
    const error = this.rejection(label);
    if (error) this.inputError.set(error);
    else this.commit([...this.value(), label]);
  }

  remove(label: string): void {
    this.commit(this.value().filter((l) => l !== label));
  }

  type(text: string): void {
    this.text.set(text);
    this.inputError.set(null);
    this.left.set(false);
  }

  touched(): void {
    this.left.set(true);
    this.onTouched();
  }

  private rejection(label: string): string | null {
    const key = label.toLowerCase();
    if (this.value().some((l) => l.toLowerCase() === key)) return `'${label}' is already added.`;
    if (this.excluded().has(key)) {
      return `'${label}' is already the topic, document type or a flag label.`;
    }
    if (this.full()) return `At most ${this.max()} additional labels.`;
    return labelPathError(label);
  }

  private commit(next: string[]): void {
    this.value.set(next);
    this.chips.setValue(next, { emitEvent: false });
    this.onChange(next);
    this.onTouched();
    if (this.full()) this.trigger().closePanel();
  }

  private clearInput(): void {
    this.input().nativeElement.value = '';
    this.text.set('');
    this.inputError.set(null);
    this.left.set(false);
  }
}
