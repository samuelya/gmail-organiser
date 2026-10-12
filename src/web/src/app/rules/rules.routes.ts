import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Gmail filters',
    loadComponent: () => import('./rules-page.component').then((m) => m.RulesPage),
  },
];

export default routes;
