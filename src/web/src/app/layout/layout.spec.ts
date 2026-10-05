import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { BehaviorSubject, map, of } from 'rxjs';
import { SetupState } from '../setup/setup-state';
import { NAV_ITEMS } from './nav-items';
import { Layout, NAV_COLLAPSED_KEY } from './layout';

describe('Layout', () => {
  let wide: BehaviorSubject<boolean>;
  let showBanner: ReturnType<typeof signal<boolean>>;
  let dismissBanner: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    localStorage.clear();
    wide = new BehaviorSubject(true);
    showBanner = signal(false);
    dismissBanner = vi.fn(() => showBanner.set(false));
    const fakeBreakpoints: Pick<BreakpointObserver, 'observe' | 'isMatched'> = {
      observe: () => wide.pipe(map((matches): BreakpointState => ({ matches, breakpoints: {} }))),
      isMatched: () => wide.value,
    };
    await TestBed.configureTestingModule({
      imports: [Layout],
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: BreakpointObserver, useValue: fakeBreakpoints },
        {
          provide: SetupState,
          useValue: { showBanner, dismissBanner, load: () => of(null) },
        },
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
