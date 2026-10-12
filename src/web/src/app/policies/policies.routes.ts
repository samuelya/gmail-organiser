import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Sender policies',
    loadComponent: () => import('./policies-page.component').then((m) => m.PoliciesPage),
  },
  {
    path: ':id',
    title: 'Sender policy',
    loadComponent: () => import('./policy-detail.component').then((m) => m.PolicyDetail),
  },
];

export default routes;
