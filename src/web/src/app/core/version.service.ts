import { HttpClient, HttpContext, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { catchError, map, Observable, of, throwError } from 'rxjs';
import { QUIET_STATUSES } from './error.interceptor';

/** `HealthDto`: `version` is the api's informational version without the `+<sha>` suffix. */
export interface Health {
  status: string;
  version: string;
}

/** The running api's version, from `GET /healthz`. */
@Injectable({ providedIn: 'root' })
export class VersionService {
  private readonly http = inject(HttpClient);

  /** A 503 (database down) still carries the version in its body; no snackbar for it. */
  getVersion(): Observable<string> {
    return this.http
      .get<Health>('/healthz', { context: new HttpContext().set(QUIET_STATUSES, [503]) })
      .pipe(
        catchError((error: unknown) =>
          error instanceof HttpErrorResponse && error.status === 503
            ? of(error.error as Health)
            : throwError(() => error),
        ),
        map((health) => health.version),
      );
  }
}
