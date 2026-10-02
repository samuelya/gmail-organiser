import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Dashboard',
    loadComponent: () => import('./dashboard-page.component').then((m) => m.DashboardPage),
  },
];

export default routes;
