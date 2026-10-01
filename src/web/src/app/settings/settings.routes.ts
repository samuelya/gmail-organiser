import { Routes } from '@angular/router';

const routes: Routes = [
  {
    path: '',
    title: 'Settings',
    loadComponent: () => import('./settings-page.component').then((m) => m.SettingsPage),
  },
];

export default routes;
