import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Senders',
    loadComponent: () => import('./senders-page.component').then((m) => m.SendersPage),
  },
];

export default routes;
