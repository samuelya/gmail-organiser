import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Review',
    loadComponent: () => import('./review-page.component').then((m) => m.ReviewPage),
  },
];

export default routes;
