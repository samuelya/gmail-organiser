import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, throwError } from 'rxjs';

export const API_UNREACHABLE = 'API unreachable';

interface ProblemDetails {
  title?: string;
  detail?: string;
}

/** Shows API errors in a snackbar and rethrows them so callers can still react. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const snackBar = inject(MatSnackBar);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        snackBar.open(errorMessage(error), 'Dismiss', { duration: 6000 });
      }
      return throwError(() => error);
    }),
  );
};

/** Maps an HTTP error (RFC 9457 ProblemDetails where available) to a user-facing message. */
export function errorMessage(error: HttpErrorResponse): string {
  if (error.status === 0) return API_UNREACHABLE;
  const problem = asProblem(error.error);
  const title = problem?.title?.trim();
  const detail = problem?.detail?.trim();
  if (title && detail) return `${title}: ${detail}`;
  if (title || detail) return (title || detail) as string;
  return error.statusText
    ? `${error.status} ${error.statusText}`
    : `Request failed (${error.status})`;
}

function asProblem(body: unknown): ProblemDetails | null {
  if (typeof body === 'string') {
    try {
      return asProblem(JSON.parse(body));
    } catch {
      return null;
    }
  }
  if (body && typeof body === 'object') {
    const { title, detail } = body as Record<string, unknown>;
    return {
      title: typeof title === 'string' ? title : undefined,
      detail: typeof detail === 'string' ? detail : undefined,
    };
  }
  return null;
}
