import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Review',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Review', milestone: 'M3' },
  },
];

export default routes;
