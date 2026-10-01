import { computed, inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, finalize, map, Observable, of, shareReplay, tap } from 'rxjs';
import { SetupService, SetupStatus } from './setup.service';

export const BANNER_DISMISSED_KEY = 'gmo.setupBannerDismissed';

/**
 * The setup status shared by the guard and the layout banner. Loaded once per app start (the
 * status pings Ollama, so navigation must not wait on it again); `refresh()` after a change.
 */
@Injectable({ providedIn: 'root' })
export class SetupState {
  private readonly setup = inject(SetupService);
  private inFlight: Observable<SetupStatus | null> | null = null;

  /** `null` until loaded, or when the status call failed. */
  readonly status = signal<SetupStatus | null>(null);
  private readonly dismissed = signal(readSession(BANNER_DISMISSED_KEY) === 'true');

  /** Setup incomplete but the wizard was seen once: the layout shows a banner instead of redirecting. */
  readonly showBanner = computed(() => {
    const status = this.status();
    return !!status && !status.complete && status.wizardSeen && !this.dismissed();
  });

  /** The cached status, or one shared request for it. Never errors: a failure yields `null`. */
  load(): Observable<SetupStatus | null> {
    if (this.status()) return of(this.status());
    return this.refresh();
  }

  refresh(): Observable<SetupStatus | null> {
    this.inFlight ??= this.setup.getSetupStatus().pipe(
      tap((status) => this.status.set(status)),
      catchError(() => of(null)),
      finalize(() => (this.inFlight = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inFlight;
  }

  /** Hides the banner for the rest of the browser session. */
  dismissBanner(): void {
    this.dismissed.set(true);
    writeSession(BANNER_DISMISSED_KEY, 'true');
  }
}

/**
 * On every page except `/setup` and `/settings`: until setup is complete and while the wizard has
 * never been seen, go to `/setup`. Once seen, the layout's banner takes over. A failed status
 * call lets the navigation through.
 */
export const setupGuard: CanActivateFn = () => {
  const router = inject(Router);
  return inject(SetupState)
    .load()
    .pipe(
      map((status) =>
        status && !status.complete && !status.wizardSeen ? router.parseUrl('/setup') : true,
      ),
    );
};

function readSession(key: string): string | null {
  try {
    return globalThis.sessionStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function writeSession(key: string, value: string): void {
  try {
    globalThis.sessionStorage?.setItem(key, value);
  } catch {
    // Storage blocked: the banner just stays dismissed until reload.
  }
}
