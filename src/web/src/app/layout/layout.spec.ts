import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NAV_ITEMS } from './nav-items';
import { Layout } from './layout';

describe('Layout', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [Layout],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  async function render() {
    const fixture = TestBed.createComponent(Layout);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

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
});
