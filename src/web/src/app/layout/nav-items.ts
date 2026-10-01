export interface NavItem {
  path: string;
  label: string;
  /** Material Symbols ligature name. */
  icon: string;
}

/** Side-nav entries, in display order. Each path is a lazy feature route. */
export const NAV_ITEMS: readonly NavItem[] = [
  { path: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
  { path: 'senders', label: 'Senders', icon: 'group' },
  { path: 'analyse', label: 'Analyse', icon: 'psychology' },
  { path: 'review', label: 'Review', icon: 'fact_check' },
  { path: 'clean-up', label: 'Clean-up', icon: 'cleaning_services' },
  { path: 'rules', label: 'Rules', icon: 'filter_alt' },
  { path: 'history', label: 'History', icon: 'history' },
  { path: 'setup', label: 'Setup', icon: 'rocket_launch' },
  { path: 'settings', label: 'Settings', icon: 'settings' },
];
