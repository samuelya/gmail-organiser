import { Routes } from '@angular/router';
import { MatReviewEditDialog } from './edit-suggestion-dialog.component';
import { REVIEW_EDIT_DIALOG } from './review.models';

const routes: Routes = [
  {
    path: '',
    title: 'Review',
    providers: [{ provide: REVIEW_EDIT_DIALOG, useClass: MatReviewEditDialog }],
    loadComponent: () => import('./review-page.component').then((m) => m.ReviewPage),
  },
];

export default routes;
