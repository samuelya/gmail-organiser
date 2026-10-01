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

  it('dismissing hides the banner for the session', async () => {
    getSetupStatus.mockReturnValue(of(status({ wizardSeen: true })));
    const state = TestBed.inject(SetupState);
    await firstValueFrom(state.load());
    state.dismissBanner();
    expect(state.showBanner()).toBe(false);
    expect(sessionStorage.getItem(BANNER_DISMISSED_KEY)).toBe('true');
  });
});
