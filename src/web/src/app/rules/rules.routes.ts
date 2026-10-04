import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Rules',
    loadComponent: () => import('./rules-page.component').then((m) => m.RulesPage),
  },
];

export default routes;
