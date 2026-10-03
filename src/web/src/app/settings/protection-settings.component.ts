import { DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  linkedSignal,
  OnInit,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, concatMap, map, merge, of } from 'rxjs';
import { SenderDto, SenderQuery, normaliseAllowlistAddress } from '../senders/senders.models';
import { SendersService } from '../senders/senders.service';
import { PROTECTION_RULES, ProtectionRule, ProtectionSettings } from './settings.models';
import { SettingsService } from './settings.service';

/** The allowlist shows this many senders per "Load more"; the Senders page search covers the rest. */
export const ALLOWLIST_PAGE_SIZE = 50;

const ALLOWLIST_QUERY: Omit<SenderQuery, 'page'> = {
  search: '',
  pageSize: ALLOWLIST_PAGE_SIZE,
  sort: 'address',
  dir: 'asc',
};

/**
 * The Settings page's "Protection" section (#183): the rule toggles, each saved as it changes with
 * only that rule in the request, and the sender allowlist. Saves itself; the page passes the
 * loaded rules in.
 */
@Component({
  selector: 'app-protection-settings',
  imports: [
    DecimalPipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSlideToggleModule,
  ],
  templateUrl: './protection-settings.component.html',
  styles: `
    .help {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
    .address {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProtectionSettingsSection implements OnInit {
  private readonly settingsApi = inject(SettingsService);
  private readonly senders = inject(SendersService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  /** `null` until loaded; the toggles stay disabled until then. */
  readonly settings = input<ProtectionSettings | null>(null);
  /** The rules as last saved: the loaded ones, then each save's response. */
  private readonly saved = linkedSignal(() => this.settings());

  readonly rules = PROTECTION_RULES;
  readonly form = new FormGroup(
    Object.fromEntries(
      PROTECTION_RULES.map((r) => [r.key, new FormControl(true, { nonNullable: true })]),
    ) as Record<ProtectionRule, FormControl<boolean>>,
  );
  /** Toggle saves in flight; they run one after another. */
  readonly saving = signal(0);

  readonly allowlist = signal<SenderDto[]>([]);
  readonly allowlistTotal = signal(0);
  private readonly allowlistPage = signal(0);
  readonly allowlistLoading = signal(false);
  readonly allowlistFailed = signal(false);
  /** Addresses with an add or remove in flight. */
  readonly busy = signal<ReadonlySet<string>>(new Set());
  readonly addForm = new FormGroup({
    address: new FormControl('', { nonNullable: true, validators: addressValidator }),
  });
  readonly address = this.addForm.controls.address;

  constructor() {
    this.form.disable();
    effect(() => {
      // The input only: a save must not reset a toggle whose own save is still queued.
      const settings = this.settings();
      untracked(() => this.load(settings));
    });
    merge(
      ...PROTECTION_RULES.map(({ key }) =>
        this.form.controls[key].valueChanges.pipe(map((value) => ({ key, value }))),
      ),
    )
      .pipe(
        concatMap((change) => this.saveRule(change.key, change.value)),
        takeUntilDestroyed(),
      )
      .subscribe();
  }

  ngOnInit(): void {
    this.loadAllowlist(1);
  }

  loadMore(): void {
    if (!this.allowlistLoading()) this.loadAllowlist(this.allowlistPage() + 1);
  }

  reloadAllowlist(): void {
    this.loadAllowlist(1);
  }

  add(): void {
    const address = normaliseAllowlistAddress(this.address.value);
    if (this.address.invalid || !address) {
      this.address.markAsTouched();
      return;
    }
    if (this.isBusy(address)) return;
    this.setBusy(address, true);
    this.senders
      .setAllowlisted(address, true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (sender) => {
          this.setBusy(address, false);
          this.address.reset();
          if (!this.allowlist().some((s) => s.address === sender.address)) {
            this.allowlist.update((list) => sortedByAddress([...list, sender]));
            this.allowlistTotal.update((n) => n + 1);
          }
          this.snackBar.open(`${sender.address} is allowlisted`, undefined, { duration: 3000 });
        },
        error: (error: unknown) => {
          this.setBusy(address, false);
          const message = problemMessage(error);
          if (message) {
            this.address.setErrors({ server: message });
            this.address.markAsTouched();
          }
        },
      });
  }

  remove(sender: SenderDto): void {
    if (this.isBusy(sender.address)) return;
    this.setBusy(sender.address, true);
    this.senders
      .setAllowlisted(sender.address, false)
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the row stays.
      .subscribe({
        next: () => {
          this.setBusy(sender.address, false);
          this.allowlist.update((list) => list.filter((s) => s.address !== sender.address));
          this.allowlistTotal.update((n) => Math.max(0, n - 1));
          this.snackBar.open(`${sender.address} removed from the allowlist`, undefined, {
            duration: 3000,
          });
        },
        error: () => this.setBusy(sender.address, false),
      });
  }

  isBusy(address: string): boolean {
    return this.busy().has(address);
  }

  private saveRule(key: ProtectionRule, value: boolean) {
    this.saving.update((n) => n + 1);
    return this.settingsApi.saveProtection({ protection: { [key]: value } }).pipe(
      map((settings) => {
        this.saving.update((n) => n - 1);
        // Only this rule: a later toggle may already be queued with its own value.
        const current = this.saved();
        const protection = settings.protection;
        if (current && protection) this.saved.set({ ...current, [key]: protection[key] });
        this.snackBar.open('Protection settings saved', undefined, { duration: 3000 });
      }),
      // The error interceptor shows why; the toggle goes back to the saved value.
      catchError(() => {
        this.saving.update((n) => n - 1);
        const current = this.saved();
        if (current) this.form.controls[key].setValue(current[key], { emitEvent: false });
        return of(undefined);
      }),
    );
  }

  private load(settings: ProtectionSettings | null): void {
    if (!settings) {
      this.form.disable({ emitEvent: false });
      return;
    }
    this.form.enable({ emitEvent: false });
    this.form.setValue(settings, { emitEvent: false });
  }

  private loadAllowlist(page: number): void {
    this.allowlistLoading.set(true);
    this.allowlistFailed.set(false);
    this.senders
      .list({ ...ALLOWLIST_QUERY, page }, true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      // The error interceptor shows why; the list offers a retry.
      .subscribe({
        next: (result) => {
          this.allowlistLoading.set(false);
          this.allowlistPage.set(page);
          this.allowlistTotal.set(result.total);
          this.allowlist.update((list) =>
            page === 1 ? result.items : mergeByAddress(list, result.items),
          );
        },
        error: () => {
          this.allowlistLoading.set(false);
          this.allowlistFailed.set(true);
        },
      });
  }

  private setBusy(address: string, busy: boolean): void {
    this.busy.update((current) => {
      const next = new Set(current);
      if (busy) next.add(address);
      else next.delete(address);
      return next;
    });
  }
}

/** Blank is `required`; anything the API would reject as an address is `address`. */
function addressValidator(control: AbstractControl<string>): ValidationErrors | null {
  if (!control.value.trim()) return { required: true };
  return normaliseAllowlistAddress(control.value) ? null : { address: true };
}

/** The field message of a 400 ValidationProblem, else its ProblemDetails title. */
function problemMessage(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return null;
  const body = error.error as { title?: string; errors?: Record<string, string[]> } | null;
  return body?.errors?.['address']?.[0] ?? body?.title ?? null;
}

/** A later page appended; a sender added meanwhile is not listed twice. */
function mergeByAddress(list: SenderDto[], page: SenderDto[]): SenderDto[] {
  const known = new Set(list.map((s) => s.address));
  return sortedByAddress([...list, ...page.filter((s) => !known.has(s.address))]);
}

function sortedByAddress(list: SenderDto[]): SenderDto[] {
  return list.sort((a, b) => (a.address < b.address ? -1 : a.address > b.address ? 1 : 0));
}
