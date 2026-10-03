import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Clean-up',
    loadComponent: () => import('./clean-up-page.component').then((m) => m.CleanUpPage),
  },
];

export default routes;
