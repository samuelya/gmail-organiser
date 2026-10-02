import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Analyse',
    loadComponent: () => import('./analyse-page.component').then((m) => m.AnalysePage),
  },
];

export default routes;
