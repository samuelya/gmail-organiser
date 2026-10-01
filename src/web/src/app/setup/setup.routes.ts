import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Setup',
    loadComponent: () => import('./setup-page.component').then((m) => m.SetupPage),
  },
];

export default routes;
