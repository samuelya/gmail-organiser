import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Dashboard',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Dashboard', milestone: 'M2' },
  },
];

export default routes;
