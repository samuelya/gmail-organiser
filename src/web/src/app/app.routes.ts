import { Routes } from '@angular/router';
import { Layout } from './layout/layout';

export const routes: Routes = [
  {
    path: '',
    component: Layout,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'setup', loadChildren: () => import('./setup/setup.routes') },
      { path: 'dashboard', loadChildren: () => import('./dashboard/dashboard.routes') },
      { path: 'senders', loadChildren: () => import('./senders/senders.routes') },
      { path: 'analyse', loadChildren: () => import('./analyse/analyse.routes') },
      { path: 'review', loadChildren: () => import('./review/review.routes') },
      { path: 'clean-up', loadChildren: () => import('./clean-up/clean-up.routes') },
      { path: 'rules', loadChildren: () => import('./rules/rules.routes') },
      { path: 'history', loadChildren: () => import('./history/history.routes') },
      { path: 'settings', loadChildren: () => import('./settings/settings.routes') },
      { path: '**', redirectTo: 'dashboard' },
    ],
  },
];
