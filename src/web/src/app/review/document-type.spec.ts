import { TestBed } from '@angular/core/testing';
import { GroupCard } from './group-card.component';
import { LabelDto } from './labels.models';
import {
  applyRestRequest,
  documentTypeOptions,
  editRequest,
  outcomeOf,
  patternSummary,
  ReviewGroupDto,
  SenderPatternDto,
  SuggestionDto,
  toDocumentTypeLabel,
} from './review.models';

const label = (name: string, type = 'user'): LabelDto => ({ id: name, name, type });

describe('documentTypeOptions', () => {
  it('lists user labels 1 to 3 levels under the parent, deduplicated and ordinal sorted', () => {
    const labels = [
      label('Docs'),
      label('Docs/receipt'),
      label('Docs/Invoice'),
      label('docs/Contract'),
      label('Docs/Invoice/Old'),
      label('Docs/invoice/old'),
      label('Docs/Invoice/Old/Paper'),
      label('Docs/Invoice/Old/Paper/Scan'),
      label('Docsx/Other'),
      label('Docs/System', 'system'),
      label('Topic/Alpha'),
    ];
    expect(documentTypeOptions(labels, 'Docs')).toEqual([
      'Contract',
      'Invoice',
      'Invoice/Old',
      'Invoice/Old/Paper',
      'receipt',
    ]);
  });

  it('puts a type before its children', () => {
    const labels = [
      label('Docs/Utilities/Electricity'),
      label('Docs/Utilities'),
      label('Docs/Utility'),
    ];
    expect(documentTypeOptions(labels, 'Docs')).toEqual([
      'Utilities',
      'Utilities/Electricity',
      'Utility',
    ]);
  });
});

describe('toDocumentTypeLabel', () => {
  it('trims 1 to 3 segments into a path under the parent; blank is none', () => {
    expect(toDocumentTypeLabel('Docs', '  Invoice ')).toBe('Docs/Invoice');
    expect(toDocumentTypeLabel('Docs', 'Utilities / Electricity')).toBe(
      'Docs/Utilities/Electricity',
    );
    expect(toDocumentTypeLabel('Docs', 'A/B/C')).toBe('Docs/A/B/C');
    expect(toDocumentTypeLabel('Docs', '   ')).toBe('');
  });

  it('refuses 4+ levels and empty segments', () => {
    expect(toDocumentTypeLabel('Docs', 'A/B/C/D')).toBeNull();
    expect(toDocumentTypeLabel('Docs', 'A//B')).toBeNull();
    expect(toDocumentTypeLabel('Docs', 'A/')).toBeNull();
    expect(toDocumentTypeLabel('Docs', '/A')).toBeNull();
    expect(toDocumentTypeLabel('Docs', 'A/ /B')).toBeNull();
  });
});

const pattern = (documentTypeLabel: string | null): SenderPatternDto & { topicLabel: string } => ({
  topicLabel: 'Topic/Alpha',
  needsAction: true,
  toBeDeleted: false,
  approvals: 4,
  agreement: 0.75,
  remaining: 6,
  documentTypeLabel,
});

describe('apply rest with a document type', () => {
  const flags = { action: 'Act', delete: 'Bin' };

  it("names the pattern's document type whether or not settings have loaded", () => {
    expect(patternSummary(pattern('Docs/Invoice'), flags)).toContain(
      'label "Topic/Alpha", document type "Docs/Invoice", "Act"',
    );
    expect(patternSummary(pattern(null), flags)).toContain('label "Topic/Alpha", "Act"');
  });

  it('never re-sends the pattern\'s own type; "" only for none with the parent on', () => {
    expect(applyRestRequest(pattern('Docs/Invoice'), 'Docs')).toEqual({});
    expect(applyRestRequest(pattern('Docs/Invoice'), 'Types')).toEqual({});
    expect(applyRestRequest(pattern(null), 'Docs')).toEqual({ documentTypeLabel: '' });
    expect(applyRestRequest(pattern('Docs/Invoice'), null)).toEqual({});
    expect(applyRestRequest(pattern(null), null)).toEqual({});
  });
});

const member = (over: Partial<SuggestionDto> = {}): SuggestionDto => ({
  id: 's1',
  messageId: 'm-s1',
  subject: 'Synthetic subject',
  date: '2026-01-01T00:00:00Z',
  snippet: null,
  source: 'llm',
  topicLabel: 'Topic/Alpha',
  isNewLabel: false,
  needsAction: false,
  toBeDeleted: false,
  unsubscribeSuggested: false,
  confidence: 0.9,
  reason: '',
  status: 'pending',
  edited: false,
  protected: false,
  replaceLabels: [],
  currentLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

const group = (over: Partial<ReviewGroupDto> = {}): ReviewGroupDto => ({
  groupKey: 'g1',
  display: 'Synthetic group',
  size: 1,
  llmCount: 1,
  derivedCount: 0,
  memoryCount: 0,
  topicLabel: 'Topic/Alpha',
  needsAction: false,
  toBeDeleted: false,
  mixed: false,
  confidenceMin: 0.9,
  confidenceMax: 0.9,
  reason: '',
  members: [member()],
  truncated: false,
  replaceLabels: [],
  labelChange: 'add',
  documentTypeLabel: null,
  documentTypeIsNew: false,
  ...over,
});

describe('document-type payloads', () => {
  it("group approve's outcome carries the card's document type", () => {
    expect(outcomeOf(group({ documentTypeLabel: 'Docs/Invoice' })).documentTypeLabel).toBe(
      'Docs/Invoice',
    );
    expect(outcomeOf(group()).documentTypeLabel).toBeNull();
  });

  it('an edit sends the document type only when given', () => {
    const outcome = { topicLabel: 'Topic/Alpha', needsAction: false, toBeDeleted: false };
    expect(editRequest(member(), outcome)).not.toHaveProperty('documentTypeLabel');
    expect(editRequest(member(), outcome, undefined, '').documentTypeLabel).toBe('');
  });
});

describe('document-type chips', () => {
  async function render(g: ReviewGroupDto) {
    const fixture = TestBed.createComponent(GroupCard);
    fixture.componentRef.setInput('group', g);
    fixture.componentRef.setInput('senderAddress', 'news@example.com');
    fixture.componentRef.setInput('labels', { action: 'Act', delete: 'Bin' });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLElement>('[data-testid="group-expand"]')!.click();
    await fixture.whenStable();
    return [...el.querySelectorAll<HTMLElement>('[data-testid="document-type"]')];
  }

  it('shows nothing without a document type', async () => {
    expect(await render(group())).toHaveLength(0);
  });

  it('shows an existing type on the card and the member', async () => {
    const chips = await render(
      group({
        documentTypeLabel: 'Docs/Invoice',
        members: [member({ documentTypeLabel: 'Docs/Invoice' })],
      }),
    );
    expect(chips.map((c) => c.textContent!.trim())).toEqual(['Docs/Invoice', 'Docs/Invoice']);
    expect(chips.some((c) => c.querySelector('[data-testid="document-type-new"]'))).toBe(false);
  });

  it('shows a nested type in full', async () => {
    const chips = await render(
      group({
        documentTypeLabel: 'Docs/Utilities/Electricity',
        members: [member({ documentTypeLabel: 'Docs/Utilities/Electricity' })],
      }),
    );
    expect(chips.map((c) => c.textContent!.trim())).toEqual([
      'Docs/Utilities/Electricity',
      'Docs/Utilities/Electricity',
    ]);
  });

  it('marks a new type', async () => {
    const chips = await render(
      group({
        documentTypeLabel: 'Docs/Invoice',
        documentTypeIsNew: true,
        members: [member({ documentTypeLabel: 'Docs/Invoice', documentTypeIsNew: true })],
      }),
    );
    expect(chips.every((c) => c.querySelector('[data-testid="document-type-new"]'))).toBe(true);
  });
});
