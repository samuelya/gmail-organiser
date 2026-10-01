import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Setup',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Setup', milestone: 'M1' },
  },
];

export default routes;
