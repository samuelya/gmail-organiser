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
  /** Incremented per refresh; only the newest request may set `status`. */
  private generation = 0;

  /** `null` until loaded, or when the status call failed. */
  readonly status = signal<SetupStatus | null>(null);
  private readonly dismissed = signal(readSession(BANNER_DISMISSED_KEY) === 'true');

  /** Setup incomplete but the wizard was seen once: the layout shows a banner instead of redirecting. */
  readonly showBanner = computed(() => {
    const status = this.status();
    return !!status && !status.complete && status.wizardSeen && !this.dismissed();
  });

  /** The cached status, or the latest pending request for it. Never errors: a failure yields `null`. */
  load(): Observable<SetupStatus | null> {
    if (this.status()) return of(this.status());
    return this.inFlight ?? this.refresh();
  }

  /**
   * Always starts a new request, so the result reflects every change saved before the call. A
   * response from an older request that arrives later does not overwrite the newer one.
   */
  refresh(): Observable<SetupStatus | null> {
    const generation = ++this.generation;
    const request: Observable<SetupStatus | null> = this.setup.getSetupStatus().pipe(
      tap((status) => {
        if (generation === this.generation) this.status.set(status);
      }),
      catchError(() => of(null)),
      finalize(() => {
        if (this.inFlight === request) this.inFlight = null;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.inFlight = request;
    return request;
  }

  /**
   * The wizard-seen flag was saved: update the cached status at once, so the guard does not
   * redirect even if the following refresh fails. Supersedes any older pending request.
   */
  markWizardSeen(): void {
    this.generation++;
    const status = this.status();
    if (status) this.status.set({ ...status, wizardSeen: true });
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
