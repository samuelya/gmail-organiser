import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Senders',
    loadComponent: () => import('./senders-page.component').then((m) => m.SendersPage),
  },
  {
    path: 'noisy',
    title: 'Noisy senders',
    loadComponent: () =>
      import('./noisy-senders-page.component').then((m) => m.NoisySendersPage),
  },
];

export default routes;
