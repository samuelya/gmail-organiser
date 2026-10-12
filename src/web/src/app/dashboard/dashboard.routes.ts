import { Routes } from '@angular/router';
import {
  ArcElement,
  BarController,
  BarElement,
  CategoryScale,
  DoughnutController,
  Legend,
  LinearScale,
  LineController,
  LineElement,
  PointElement,
  Tooltip,
} from 'chart.js';
import { provideCharts } from 'ng2-charts';

const routes: Routes = [
  {
    path: '',
    title: 'Dashboard',
    // Chart.js is registered here only, with just the pieces the dashboard charts use, to keep it out of the initial bundle.
    providers: [
      provideCharts({
        registerables: [
          BarController,
          BarElement,
          LineController,
          LineElement,
          PointElement,
          DoughnutController,
          ArcElement,
          CategoryScale,
          LinearScale,
          Tooltip,
          Legend,
        ],
      }),
    ],
    loadComponent: () => import('./dashboard-page.component').then((m) => m.DashboardPage),
  },
];

export default routes;
