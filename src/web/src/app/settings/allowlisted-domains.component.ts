import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatChipInputEvent, MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAX_ALLOWLISTED_DOMAINS, normaliseAllowlistDomain } from './settings.models';
import { SettingsService } from './settings.service';

/** The API's error key for the domain list. */
const DOMAINS_FIELD = 'protection.allowlistedDomains';

/**
 * "Allowlisted domains" in the Settings Protection section (#204): a chip per domain, added on
 * Enter or comma and removed per chip. Each change saves the whole list, like the rule toggles.
 */
@Component({
  selector: 'app-allowlisted-domains',
  imports: [ReactiveFormsModule, MatChipsModule, MatFormFieldModule, MatIconModule],
  template: `
    <mat-form-field class="w-full" subscriptSizing="dynamic">
      <mat-label>Allowlisted domains</mat-label>
      <mat-chip-grid
        #grid
        [formControl]="domains"
        aria-label="Allowlisted domains"
        data-testid="domain-grid"
      >
        @for (domain of domains.value; track domain) {
          <mat-chip-row (removed)="remove(domain)" [disabled]="saving()" data-testid="domain-chip">
            {{ domain }}
            <button
              matChipRemove
              [attr.aria-label]="'Remove ' + domain"
              data-testid="domain-remove"
            >
              <mat-icon aria-hidden="true">cancel</mat-icon>
            </button>
          </mat-chip-row>
        }
        <input
          [matChipInputFor]="grid"
          [matChipInputSeparatorKeyCodes]="separators"
          (matChipInputTokenEnd)="add($event)"
          (input)="clearError()"
          [disabled]="saving()"
          autocomplete="off"
          placeholder="example.com"
          data-testid="domain-input"
        />
      </mat-chip-grid>
      @if (error(); as error) {
        <mat-error data-testid="domain-error">{{ error }}</mat-error>
      }
      <mat-hint
        >Mail from these domains and their subdomains is never marked for deletion.</mat-hint
      >
    </mat-form-field>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AllowlistedDomains {
  private readonly settingsApi = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** The saved list; `null` until loaded, which keeps the field disabled. */
  readonly listed = input<readonly string[] | null>(null);

  readonly separators = [ENTER, COMMA] as const;
  /** The field's message; kept here so the chip grid's blur re-validation can't clear it. */
  readonly error = signal<string | null>(null);
  /** The saved list, shown as chips; invalid while there is a message. */
  readonly domains = new FormControl<string[]>([], {
    nonNullable: true,
    validators: () => {
      const message = this.error();
      return message ? { domain: message } : null;
    },
  });
  readonly saving = signal(false);

  constructor() {
    this.domains.disable();
    effect(() => {
      const listed = this.listed();
      untracked(() => {
        if (!listed) return;
        this.domains.setValue([...listed]);
        this.domains.enable();
      });
    });
  }

  /** A pasted list may hold several domains; any invalid one keeps the text for editing. */
  add(event: MatChipInputEvent): void {
    const entries = event.value.split(/[\s,]+/).filter((v) => v);
    if (entries.length === 0 || this.saving()) return;
    const normalised = entries.map((v) => normaliseAllowlistDomain(v));
    const bad = entries.find((_, i) => !normalised[i]);
    if (bad !== undefined) {
      this.showError(
        `'${bad}' is not a domain: enter a host name such as example.com, without '@'.`,
      );
      return;
    }
    const current = this.domains.value;
    const next = [...new Set([...current, ...(normalised as string[])])];
    if (next.length > MAX_ALLOWLISTED_DOMAINS) {
      this.showError(`At most ${MAX_ALLOWLISTED_DOMAINS} domains.`);
      return;
    }
    if (next.length === current.length) {
      event.chipInput.clear();
      return;
    }
    this.save(next, 'Allowlisted domains saved', () => event.chipInput.clear());
  }

  remove(domain: string): void {
    if (this.saving()) return;
    this.save(
      this.domains.value.filter((d) => d !== domain),
      `${domain} removed from the allowlist`,
    );
  }

  /** Typing again drops the last message. */
  clearError(): void {
    if (this.error() === null) return;
    this.error.set(null);
    this.domains.updateValueAndValidity();
  }

  private save(next: string[], message: string, done?: () => void): void {
    this.saving.set(true);
    this.settingsApi
      .saveProtection({ protection: { allowlistedDomains: next } })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.error.set(null);
          this.domains.setValue(settings.protection?.allowlistedDomains ?? next);
          done?.();
          this.snackBar.open(message, undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.saving.set(false);
          // A 400 explains itself here; anything else the error interceptor shows.
          const message = problemMessage(error);
          if (message) this.showError(message);
        },
      });
  }

  private showError(message: string): void {
    this.error.set(message);
    this.domains.updateValueAndValidity();
    this.domains.markAsTouched();
  }
}

function problemMessage(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return null;
  const body = error.error as { title?: string; errors?: Record<string, string[]> } | null;
  return body?.errors?.[DOMAINS_FIELD]?.[0] ?? body?.title ?? null;
}
