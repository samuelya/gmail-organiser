import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Clean-up',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Clean-up', milestone: 'M5' },
  },
];

export default routes;
