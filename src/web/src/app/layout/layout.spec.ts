import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import { BehaviorSubject, map, of } from 'rxjs';
import { JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SetupState } from '../setup/setup-state';
import { SetupStatus } from '../setup/setup.service';
import { NAV_ITEMS } from './nav-items';
import { Layout, NAV_COLLAPSED_KEY } from './layout';

describe('Layout', () => {
  let wide: BehaviorSubject<boolean>;
  let showBanner: ReturnType<typeof signal<boolean>>;
  let dismissBanner: ReturnType<typeof vi.fn>;
  let status: ReturnType<typeof signal<SetupStatus | null>>;
  let activeJobs: ReturnType<typeof signal<JobDto[]>>;
  let connectionState: ReturnType<typeof signal<JobsConnectionState>>;

  const job = (id: string, type: string, done: number, total: number | null): JobDto => ({
    id,
    type,
    queue: 'default',
    status: 'running',
    progress: { done, total, message: null },
    error: null,
    createdAt: '2026-01-01T00:00:00Z',
    startedAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    finishedAt: null,
    version: 1,
  });

  const setupStatus = (completedOnce: boolean, complete: boolean): SetupStatus => ({
    googleClientConfigured: true,
    gmailConnected: complete,
    gmailReauthRequired: !complete,
    ollamaReachable: true,
    chatModelSelected: true,
    embeddingModelSelected: true,
    wizardSeen: true,
    complete,
    completedOnce,
  });

  beforeEach(async () => {
    localStorage.clear();
    wide = new BehaviorSubject(true);
    showBanner = signal(false);
    dismissBanner = vi.fn(() => showBanner.set(false));
    status = signal<SetupStatus | null>(null);
    activeJobs = signal<JobDto[]>([]);
    connectionState = signal<JobsConnectionState>('connected');
    const fakeBreakpoints: Pick<BreakpointObserver, 'observe' | 'isMatched'> = {
      observe: () => wide.pipe(map((matches): BreakpointState => ({ matches, breakpoints: {} }))),
      isMatched: () => wide.value,
    };
    await TestBed.configureTestingModule({
      imports: [Layout],
      deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: BreakpointObserver, useValue: fakeBreakpoints },
        {
          provide: SetupState,
          useValue: { showBanner, dismissBanner, status, load: () => of(null) },
        },
        { provide: JobsService, useValue: { activeJobs, connectionState } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    }).compileComponents();
  });

  async function render() {
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const menuButton = (el: HTMLElement) =>
    el.querySelector<HTMLButtonElement>('button[aria-controls="app-nav"]')!;

  it('renders every nav item with icon, label and link', async () => {
    const el = await render();
    const links = Array.from(el.querySelectorAll<HTMLAnchorElement>('nav a[mat-list-item]'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(NAV_ITEMS.map((i) => `/${i.path}`));
    NAV_ITEMS.forEach((item, i) => {
      expect(links[i].textContent).toContain(item.label);
      expect(links[i].querySelector('mat-icon')?.textContent?.trim()).toBe(item.icon);
    });
  });

  const navPaths = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('nav a[mat-list-item]')).map((a) => a.getAttribute('href'));
  const jobsButton = (el: HTMLElement) =>
    el.querySelector<HTMLButtonElement>('[data-testid="jobs-button"]');

  it('shows the Setup entry while the setup status is unknown', async () => {
    expect(navPaths(await render())).toContain('/setup');
  });

  it('shows the Setup entry until setup has been completed once', async () => {
    status.set(setupStatus(false, false));
    expect(navPaths(await render())).toContain('/setup');
  });

  it('hides the Setup entry once setup has been completed', async () => {
    status.set(setupStatus(true, true));
    const paths = navPaths(await render());
    expect(paths).not.toContain('/setup');
    expect(paths).toContain('/settings');
  });

  it('keeps the Setup entry hidden when setup later becomes incomplete', async () => {
    status.set(setupStatus(true, false));
    expect(navPaths(await render())).not.toContain('/setup');
  });

  it('does not start the jobs indicator before Gmail is connected or setup completed', async () => {
    status.set({ ...setupStatus(false, false), gmailConnected: false });
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    expect(await fixture.getDeferBlocks()).toHaveLength(0);
  });

  it('starts the jobs indicator once Gmail is connected', async () => {
    status.set({ ...setupStatus(false, false), gmailConnected: true });
    activeJobs.set([job('j1', 'fetch', 1, 10)]);
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    const [block] = await fixture.getDeferBlocks();
    await block.render(DeferBlockState.Complete);
    expect(jobsButton(fixture.nativeElement)?.getAttribute('aria-label')).toBe('1 job running');
  });

  it('offers a reload when the jobs indicator fails to load', async () => {
    status.set(setupStatus(true, true));
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    const [block] = await fixture.getDeferBlocks();
    await block.render(DeferBlockState.Error);
    const el = fixture.nativeElement as HTMLElement;
    expect(jobsButton(el)).toBeNull();
    expect(
      el.querySelector('[data-testid="jobs-unavailable"]')?.getAttribute('aria-label'),
    ).toContain('reload');
  });

  it('shows the setup banner and hides it on dismiss', async () => {
    showBanner.set(true);
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const banner = () => el.querySelector('[data-testid="setup-banner"]');
    expect(banner()?.textContent).toContain('Setup incomplete');
    expect(banner()?.querySelector('a')?.getAttribute('href')).toBe('/setup');
    el.querySelector<HTMLButtonElement>('[data-testid="setup-banner-dismiss"]')!.click();
    await fixture.whenStable();
    expect(dismissBanner).toHaveBeenCalled();
    expect(banner()).toBeNull();
  });

  it('hides the setup banner on the wizard itself', async () => {
    showBanner.set(true);
    await TestBed.inject(Router).navigateByUrl('/setup');
    const el = await render();
    expect(el.querySelector('[data-testid="setup-banner"]')).toBeNull();
  });

  it('includes the ten feature pages and the noisy senders page', () => {
    expect(NAV_ITEMS.map((i) => i.path).sort()).toEqual(
      [
        'analyse',
        'clean-up',
        'dashboard',
        'history',
        'policies',
        'review',
        'rules',
        'settings',
        'senders',
        'senders/noisy',
        'setup',
      ].sort(),
    );
  });

  it('shows the app name, theme toggle and skip link', async () => {
    const el = await render();
    expect(el.querySelector('.app-name')?.textContent).toContain('Gmail Organiser');
    expect(el.querySelector('.theme-toggle')?.getAttribute('aria-label')).toContain('Theme');
    expect(el.querySelector('.skip-link')?.textContent).toContain('Skip to content');
  });

  it('skip link moves focus to the main content', async () => {
    const el = await render();
    (el.querySelector('.skip-link') as HTMLAnchorElement).click();
    expect(document.activeElement?.id).toBe('main-content');
  });

  it('collapses the docked nav state when Escape closes it on a wide screen', async () => {
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(menuButton(el).getAttribute('aria-expanded')).toBe('true');

    el.querySelector('mat-sidenav')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(menuButton(el).getAttribute('aria-expanded')).toBe('false');
    expect(localStorage.getItem(NAV_COLLAPSED_KEY)).toBe('true');

    menuButton(el).click();
    await fixture.whenStable();
    expect(menuButton(el).getAttribute('aria-expanded')).toBe('true');
  });

  it('keeps the overlay closed after resizing wide and back to narrow', async () => {
    wide.next(false);
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    menuButton(el).click();
    await fixture.whenStable();
    expect(menuButton(el).getAttribute('aria-expanded')).toBe('true');

    wide.next(true);
    await fixture.whenStable();
    wide.next(false);
    await fixture.whenStable();
    expect(menuButton(el).getAttribute('aria-expanded')).toBe('false');
  });
});
