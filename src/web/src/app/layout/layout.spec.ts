import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, map } from 'rxjs';
import { NAV_ITEMS } from './nav-items';
import { Layout, NAV_COLLAPSED_KEY } from './layout';

describe('Layout', () => {
  let wide: BehaviorSubject<boolean>;

  beforeEach(async () => {
    localStorage.clear();
    wide = new BehaviorSubject(true);
    const fakeBreakpoints: Pick<BreakpointObserver, 'observe' | 'isMatched'> = {
      observe: () =>
        wide.pipe(map((matches): BreakpointState => ({ matches, breakpoints: {} }))),
      isMatched: () => wide.value,
    };
    await TestBed.configureTestingModule({
      imports: [Layout],
      providers: [provideRouter([]), { provide: BreakpointObserver, useValue: fakeBreakpoints }],
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

  it('includes the nine feature pages', () => {
    expect(NAV_ITEMS.map((i) => i.path).sort()).toEqual(
      [
        'analyse',
        'clean-up',
        'dashboard',
        'history',
        'review',
        'rules',
        'settings',
        'senders',
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
