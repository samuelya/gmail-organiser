import { BreakpointObserver } from '@angular/cdk/layout';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { distinctUntilChanged, filter, map, startWith, tap } from 'rxjs';
import { readLocal, writeLocal } from '../core/local-store';
import { ThemeChoice, ThemeService } from '../core/theme.service';
import { SetupState } from '../setup/setup-state';
import { NAV_ITEMS, navActiveOptions } from './nav-items';

export const WIDE_QUERY = '(min-width: 1024px)';
export const NAV_COLLAPSED_KEY = 'gmo.navCollapsed';

const THEME_ICONS: Record<ThemeChoice, string> = {
  system: 'brightness_auto',
  light: 'light_mode',
  dark: 'dark_mode',
};

/** App shell: toolbar, collapsible side nav (side ≥1024 px, over below) and the routed content area. */
@Component({
  selector: 'app-layout',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatListModule,
    MatSidenavModule,
    MatToolbarModule,
    MatTooltipModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
  ],
  templateUrl: './layout.html',
  styleUrl: './layout.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Layout {
  protected readonly appName = 'Gmail Organiser';
  protected readonly navItems = NAV_ITEMS.map((item) => ({
    ...item,
    active: navActiveOptions(item),
  }));
  protected readonly theme = inject(ThemeService);
  protected readonly setupState = inject(SetupState);
  private readonly router = inject(Router);

  private readonly breakpoints = inject(BreakpointObserver);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  /** Persisted collapse state for the docked (side) nav. */
  private readonly collapsed = signal(readLocal(NAV_COLLAPSED_KEY) === 'true');
  /** Overlay nav state on narrow screens; starts closed and resets on every breakpoint change. */
  private readonly overlayOpen = signal(false);

  protected readonly isWide = toSignal(
    this.breakpoints.observe(WIDE_QUERY).pipe(
      map((s) => s.matches),
      distinctUntilChanged(),
      tap(() => this.overlayOpen.set(false)),
    ),
    {
      initialValue: this.breakpoints.isMatched(WIDE_QUERY),
    },
  );

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  /** "Setup incomplete" banner, except on the wizard itself. */
  protected readonly showSetupBanner = computed(
    () => this.setupState.showBanner() && !this.url().startsWith('/setup'),
  );

  constructor() {
    // The guard loads the status on guarded pages; this covers landing on /settings directly.
    this.setupState.load().subscribe();
  }

  protected readonly navOpen = computed(() =>
    this.isWide() ? !this.collapsed() : this.overlayOpen(),
  );
  protected readonly themeIcon = computed(() => THEME_ICONS[this.theme.choice()]);
  protected readonly themeLabel = computed(() => `Theme: ${this.theme.choice()} (change)`);

  protected toggleNav(): void {
    if (this.isWide()) {
      this.setCollapsed(!this.collapsed());
    } else {
      this.overlayOpen.update((open) => !open);
    }
  }

  /**
   * Keeps state in sync when the drawer closes itself: backdrop click or Escape on the overlay,
   * Escape on the docked nav (MatDrawer handles Escape in `side` mode too).
   */
  protected onOpenedChange(open: boolean): void {
    if (this.isWide()) {
      if (this.collapsed() === open) this.setCollapsed(!open);
    } else {
      this.overlayOpen.set(open);
    }
  }

  private setCollapsed(collapsed: boolean): void {
    this.collapsed.set(collapsed);
    writeLocal(NAV_COLLAPSED_KEY, String(collapsed));
  }

  protected onNavigate(): void {
    if (!this.isWide()) this.overlayOpen.set(false);
  }

  /** In-page skip link; a plain `#fragment` href would resolve against <base href> and reload. */
  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.main().nativeElement.focus();
  }
}
