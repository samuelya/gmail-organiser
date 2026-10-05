import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Policies',
    loadComponent: () => import('./policies-page.component').then((m) => m.PoliciesPage),
  },
  {
    path: ':id',
    title: 'Policy',
    loadComponent: () => import('./policy-detail.component').then((m) => m.PolicyDetail),
  },
];

export default routes;
