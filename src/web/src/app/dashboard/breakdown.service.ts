import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { MailboxBreakdownDto } from './breakdown.models';

/** Mailbox mix API: messages per Gmail category tab and suggestions per mail type. */
@Injectable({ providedIn: 'root' })
export class BreakdownService {
  private readonly http = inject(HttpClient);

  get(): Observable<MailboxBreakdownDto> {
    return this.http.get<MailboxBreakdownDto>('/api/dashboard/breakdown');
  }
}
