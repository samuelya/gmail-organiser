import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Rules',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Rules', milestone: 'M6' },
  },
];

export default routes;
