import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Senders',
    loadComponent: () => import('../layout/placeholder-page').then((m) => m.PlaceholderPage),
    data: { heading: 'Senders', milestone: 'M2' },
  },
];

export default routes;
