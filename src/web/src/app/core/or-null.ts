import { catchError, map, Observable, of } from 'rxjs';

/** Errors become `null`; the error interceptor already told the user. */
export function orNull<T>() {
  return (source: Observable<T>) =>
    source.pipe(
      map((value) => value as T | null),
      catchError(() => of(null)),
    );
}
