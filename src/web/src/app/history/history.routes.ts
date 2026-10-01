import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'History',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'History', milestone: 'M3' },
  },
];

export default routes;
