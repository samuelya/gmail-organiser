import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'History',
    loadComponent: () => import('./history-page.component').then((m) => m.HistoryPage),
  },
];

export default routes;
