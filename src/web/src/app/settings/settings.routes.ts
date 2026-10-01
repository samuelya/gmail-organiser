import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Settings',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Settings', milestone: 'M1' },
  },
];

export default routes;
