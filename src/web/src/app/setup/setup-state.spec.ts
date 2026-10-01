import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { firstValueFrom, Observable, of, Subject, throwError } from 'rxjs';
import { BANNER_DISMISSED_KEY, setupGuard, SetupState } from './setup-state';
import { SetupService, SetupStatus } from './setup.service';

function status(over: Partial<SetupStatus>): SetupStatus {
  return {
    googleClientConfigured: true,
    gmailConnected: false,
    gmailReauthRequired: false,
    ollamaReachable: false,
    chatModelSelected: false,
    embeddingModelSelected: false,
    wizardSeen: false,
    complete: false,
    ...over,
  };
}

describe('SetupState and setupGuard', () => {
  let getSetupStatus: ReturnType<typeof vi.fn<() => Observable<SetupStatus>>>;

  beforeEach(() => {
    sessionStorage.clear();
    getSetupStatus = vi.fn<() => Observable<SetupStatus>>(() => of(status({})));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SetupService, useValue: { getSetupStatus } }],
    });
  });

  async function runGuard(): Promise<true | UrlTree> {
    const result = TestBed.runInInjectionContext(() =>
      setupGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
    );
    return firstValueFrom(result as Observable<true | UrlTree>);
  }

  it('incomplete and the wizard never seen: redirects to /setup', async () => {
    const result = await runGuard();
    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/setup');
  });

  it('incomplete but the wizard seen: lets through and shows the banner', async () => {
    getSetupStatus.mockReturnValue(of(status({ wizardSeen: true })));
    expect(await runGuard()).toBe(true);
    expect(TestBed.inject(SetupState).showBanner()).toBe(true);
  });

  it('complete: lets through without a banner', async () => {
    getSetupStatus.mockReturnValue(of(status({ complete: true })));
    expect(await runGuard()).toBe(true);
    expect(TestBed.inject(SetupState).showBanner()).toBe(false);
  });

  it('a failed status call lets the navigation through', async () => {
    getSetupStatus.mockReturnValue(throwError(() => new Error('down')));
    expect(await runGuard()).toBe(true);
  });

  it('loads once and shares an in-flight request', async () => {
    const pending = new Subject<SetupStatus>();
    getSetupStatus.mockReturnValue(pending);
    const state = TestBed.inject(SetupState);
    const first = firstValueFrom(state.load());
    const second = firstValueFrom(state.load());
    pending.next(status({ complete: true }));
    pending.complete();
    await Promise.all([first, second]);
    await firstValueFrom(state.load());
    expect(getSetupStatus).toHaveBeenCalledTimes(1);
  });

  it('refresh starts a new request even while an older one is pending; the older result is dropped', async () => {
    const older = new Subject<SetupStatus>();
    const newer = new Subject<SetupStatus>();
    getSetupStatus.mockReturnValueOnce(older).mockReturnValueOnce(newer);
    const state = TestBed.inject(SetupState);
    void firstValueFrom(state.refresh());
    const fresh = firstValueFrom(state.refresh());
    expect(getSetupStatus).toHaveBeenCalledTimes(2);
    newer.next(status({ wizardSeen: true }));
    newer.complete();
    expect((await fresh)?.wizardSeen).toBe(true);
    older.next(status({ wizardSeen: false }));
    older.complete();
    expect(state.status()?.wizardSeen).toBe(true);
    expect(await runGuard()).toBe(true);
  });

  it('markWizardSeen updates the cache, so a stale or failed later status does not redirect', async () => {
    const stale = new Subject<SetupStatus>();
    getSetupStatus.mockReturnValueOnce(of(status({}))).mockReturnValueOnce(stale);
    const state = TestBed.inject(SetupState);
    await firstValueFrom(state.load());
    void firstValueFrom(state.refresh()); // e.g. the summary step, started before Finish saved
    state.markWizardSeen();
    getSetupStatus.mockReturnValue(throwError(() => new Error('down')));
    expect(await firstValueFrom(state.refresh())).toBeNull();
    stale.next(status({ wizardSeen: false }));
    stale.complete();
    expect(state.status()?.wizardSeen).toBe(true);
    expect(await runGuard()).toBe(true);
  });

  it('dismissing hides the banner for the session', async () => {
    getSetupStatus.mockReturnValue(of(status({ wizardSeen: true })));
    const state = TestBed.inject(SetupState);
    await firstValueFrom(state.load());
    state.dismissBanner();
    expect(state.showBanner()).toBe(false);
    expect(sessionStorage.getItem(BANNER_DISMISSED_KEY)).toBe('true');
  });
});
