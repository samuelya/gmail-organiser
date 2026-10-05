import { Routes } from '@angular/router';
import { Layout } from './layout/layout';
import { setupGuard } from './setup/setup-state';

/** `/setup` and `/settings` stay reachable while setup is incomplete; every other page is guarded. */
export const routes: Routes = [
  {
    path: '',
    component: Layout,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'setup', loadChildren: () => import('./setup/setup.routes') },
      { path: 'settings', loadChildren: () => import('./settings/settings.routes') },
      {
        path: '',
        canActivateChild: [setupGuard],
        children: [
          { path: 'dashboard', loadChildren: () => import('./dashboard/dashboard.routes') },
          { path: 'senders', loadChildren: () => import('./senders/senders.routes') },
          { path: 'analyse', loadChildren: () => import('./analyse/analyse.routes') },
          { path: 'review', loadChildren: () => import('./review/review.routes') },
          { path: 'policies', loadChildren: () => import('./policies/policies.routes') },
          { path: 'clean-up', loadChildren: () => import('./clean-up/clean-up.routes') },
          { path: 'rules', loadChildren: () => import('./rules/rules.routes') },
          { path: 'history', loadChildren: () => import('./history/history.routes') },
        ],
      },
      { path: '**', redirectTo: 'dashboard' },
    ],
  },
];
