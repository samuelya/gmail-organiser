import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { JobsService } from '../core/jobs.service';
import { PoliciesService } from './policies.service';
import { PoliciesPage } from './policies-page.component';

describe('PoliciesPage', () => {
  it('does not navigate when the tab index follows a URL change', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'policies', component: PoliciesPage }]),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: JobsService, useValue: { reconnects: signal(0) } },
        {
          provide: PoliciesService,
          useValue: {
            list: vi.fn(() => of({ items: [], total: 0, page: 1, pageSize: 25 })),
            counts: vi.fn(() => of({ proposed: 0, approved: 0, rejected: 0 })),
          },
        },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl('/policies?status=approved&page=3', PoliciesPage);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');

    page.onTab(page.statuses.indexOf('approved'));
    expect(navigate).not.toHaveBeenCalled();
    expect(router.url).toContain('page=3');

    page.onTab(page.statuses.indexOf('proposed'));
    expect(navigate).toHaveBeenCalledTimes(1);
  });
});
