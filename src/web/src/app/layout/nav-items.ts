import { IsActiveMatchOptions } from '@angular/router';

export interface NavItem {
  path: string;
  label: string;
  /** Material Symbols ligature name. */
  icon: string;
  /** Shown indented under the entry before it. */
  nested?: boolean;
}

/** Side-nav entries, in display order. Each path is a lazy feature route. */
export const NAV_ITEMS: readonly NavItem[] = [
  { path: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
  { path: 'senders', label: 'Senders', icon: 'group' },
  { path: 'senders/noisy', label: 'Noisy senders', icon: 'campaign', nested: true },
  { path: 'analyse', label: 'Analyse', icon: 'psychology' },
  { path: 'review', label: 'Review', icon: 'fact_check' },
  { path: 'policies', label: 'Policies', icon: 'policy' },
  { path: 'clean-up', label: 'Clean-up', icon: 'cleaning_services' },
  { path: 'rules', label: 'Rules', icon: 'filter_alt' },
  { path: 'history', label: 'History', icon: 'history' },
  { path: 'setup', label: 'Setup', icon: 'rocket_launch' },
  { path: 'settings', label: 'Settings', icon: 'settings' },
];

/**
 * How an entry matches the URL: a path and anything below it, except that an entry with a nested entry
 * matches only its own path, so one entry at a time is current. Query params never matter.
 */
export function navActiveOptions(item: NavItem): IsActiveMatchOptions {
  const parent = NAV_ITEMS.some((other) => other.path.startsWith(item.path + '/'));
  return {
    paths: parent ? 'exact' : 'subset',
    queryParams: 'ignored',
    matrixParams: 'ignored',
    fragment: 'ignored',
  };
}
