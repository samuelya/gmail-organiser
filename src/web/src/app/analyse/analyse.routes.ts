import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Analyse',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Analyse', milestone: 'M3' },
  },
];

export default routes;
