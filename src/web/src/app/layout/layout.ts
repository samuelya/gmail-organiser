import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { readLocal, writeLocal } from '../core/local-store';
import { ThemeChoice, ThemeService } from '../core/theme.service';
import { NAV_ITEMS } from './nav-items';

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
  protected readonly navItems = NAV_ITEMS;
  protected readonly theme = inject(ThemeService);

  private readonly breakpoints = inject(BreakpointObserver);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  protected readonly isWide = toSignal(this.breakpoints.observe(WIDE_QUERY).pipe(map((s) => s.matches)), {
    initialValue: this.breakpoints.isMatched(WIDE_QUERY),
  });
  /** Persisted collapse state for the docked (side) nav. */
  private readonly collapsed = signal(readLocal(NAV_COLLAPSED_KEY) === 'true');
  /** Overlay nav state on narrow screens; always starts closed. */
  private readonly overlayOpen = signal(false);

  protected readonly navOpen = computed(() => (this.isWide() ? !this.collapsed() : this.overlayOpen()));
  protected readonly themeIcon = computed(() => THEME_ICONS[this.theme.choice()]);
  protected readonly themeLabel = computed(() => `Theme: ${this.theme.choice()} (change)`);

  protected toggleNav(): void {
    if (this.isWide()) {
      const collapsed = !this.collapsed();
      this.collapsed.set(collapsed);
      writeLocal(NAV_COLLAPSED_KEY, String(collapsed));
    } else {
      this.overlayOpen.update((open) => !open);
    }
  }

  /** Keeps state in sync when the overlay closes via backdrop click or Escape. */
  protected onOpenedChange(open: boolean): void {
    if (!this.isWide()) this.overlayOpen.set(open);
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
