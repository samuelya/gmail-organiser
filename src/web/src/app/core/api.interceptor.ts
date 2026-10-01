import { HttpInterceptorFn } from '@angular/common/http';

/**
 * Marks every request as an XHR call. The API rejects state-changing requests under /api
 * without this header (anti-CSRF). API calls use relative `/api/...` URLs; the dev server
 * and nginx proxy them, so no base URL is configured here.
 */
export const apiInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ setHeaders: { 'X-Requested-With': 'XMLHttpRequest' } }));
