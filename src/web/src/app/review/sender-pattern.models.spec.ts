import { applyRestRequest, patternSummary, SenderPatternDto } from './sender-pattern.models';

const pattern = (
  extra: Partial<Omit<SenderPatternDto, 'topicLabel'>> = {},
): SenderPatternDto & { topicLabel: string } => ({
  topicLabel: 'Topic/Alpha',
  needsAction: false,
  toBeDeleted: false,
  approvals: 4,
  agreement: 0.75,
  remaining: 6,
  documentTypeLabel: null,
  ...extra,
});

describe('apply rest with additional labels', () => {
  const flags = { action: 'Act', delete: 'Bin' };

  it("lists the pattern's additional labels in the confirmation", () => {
    expect(patternSummary(pattern({ extraLabels: ['Extra/One', 'Extra/Two'] }), flags)).toContain(
      'label "Topic/Alpha", additional labels "Extra/One", "Extra/Two" for',
    );
    expect(
      patternSummary(
        pattern({
          documentTypeLabel: 'Docs/Invoice',
          needsAction: true,
          extraLabels: ['Extra/One'],
        }),
        flags,
      ),
    ).toContain(
      'label "Topic/Alpha", document type "Docs/Invoice", "Act", additional label "Extra/One" for',
    );
  });

  it('names no additional labels when the pattern has none or omits the field', () => {
    expect(patternSummary(pattern({ extraLabels: [] }), flags)).not.toContain('additional');
    expect(patternSummary(pattern(), flags)).not.toContain('additional');
  });

  it("omits extraLabels unless they differ from the pattern's", () => {
    const p = pattern({ extraLabels: ['Extra/One', 'Extra/Two'] });
    expect(applyRestRequest(p, null)).toEqual({});
    expect(applyRestRequest(p, null, ['extra/two', 'Extra/One'])).toEqual({});
    expect(applyRestRequest(pattern(), null, [])).toEqual({});
  });

  it('sends the changed set, [] to clear, next to the document type', () => {
    const p = pattern({ extraLabels: ['Extra/One'] });
    expect(applyRestRequest(p, null, ['Extra/One', 'Extra/Two'])).toEqual({
      extraLabels: ['Extra/One', 'Extra/Two'],
    });
    expect(applyRestRequest(p, null, [])).toEqual({ extraLabels: [] });
    expect(applyRestRequest(pattern(), 'Docs', ['Extra/Two'])).toEqual({
      documentTypeLabel: '',
      extraLabels: ['Extra/Two'],
    });
  });
});
