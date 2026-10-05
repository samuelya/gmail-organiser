import { ComponentRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { startMailType } from './edit-suggestion-dialog.component';
import { commonMailType, MailTypeChip, mailTypeHue } from './mail-type-chip.component';
import { filterByMailType, mailTypesParam, parseMailTypes } from './mail-type-filter';
import { editRequest, ReviewGroupDto, SuggestionDto } from './review.models';

const member = (mailType: string | null, id = 'a') => ({ id, mailType }) as SuggestionDto;
const card = (...types: (string | null)[]) =>
  ({ members: types.map((t, i) => member(t, String(i))) }) as ReviewGroupDto;

describe('MailTypeChip', () => {
  function render(type: string | null) {
    const fixture = TestBed.createComponent(MailTypeChip);
    (fixture.componentRef as ComponentRef<MailTypeChip>).setInput('type', type);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('[data-testid="mail-type-chip"]') as
      | HTMLElement
      | null;
  }

  it('shows the type spaced, with its own colour', () => {
    const chip = render('action_bill')!;
    expect(chip.textContent!.trim()).toBe('action bill');
    expect(chip.dataset['type']).toBe('action_bill');
    expect(chip.style.getPropertyValue('--mail-type-hue')).toBe(mailTypeHue('action_bill'));
    expect(mailTypeHue('receipt')).not.toBe(mailTypeHue('action_bill'));
  });

  it('shows nothing without a type and a neutral colour for an unknown one', () => {
    expect(render(null)).toBeNull();
    expect(render('unknown_type')!.style.getPropertyValue('--mail-type-hue')).toBe(
      mailTypeHue('another_unknown'),
    );
  });

  it("gives a card its members' shared type, and none when they differ", () => {
    expect(commonMailType([member('receipt'), member('receipt')])).toBe('receipt');
    expect(commonMailType([member('receipt'), member('newsletter')])).toBeNull();
    expect(commonMailType([member(null), member('receipt')])).toBeNull();
    expect(commonMailType([])).toBeNull();
  });
});

describe('mail-type filter', () => {
  it('reads known types from the query string in a fixed order', () => {
    expect(parseMailTypes('newsletter, receipt,bogus,receipt')).toEqual(['receipt', 'newsletter']);
    expect(parseMailTypes(null)).toEqual([]);
    expect(parseMailTypes('')).toEqual([]);
  });

  it('writes the selection back, dropping the parameter when empty', () => {
    expect(mailTypesParam(['newsletter', 'receipt'])).toBe('receipt,newsletter');
    expect(mailTypesParam([])).toBeNull();
    expect(mailTypesParam(['bogus'])).toBeNull();
  });

  it('keeps the groups with a listed member of a selected type', () => {
    const groups = [card('receipt'), card('newsletter', 'receipt'), card(null), card('social')];
    expect(filterByMailType(groups, [])).toBe(groups);
    expect(filterByMailType(groups, ['receipt'])).toEqual([groups[0], groups[1]]);
    expect(filterByMailType(groups, ['social', 'newsletter'])).toEqual([groups[1], groups[3]]);
  });
});

describe('edit mail type', () => {
  it('starts from the shared type, none as "", and nothing picked when the members differ', () => {
    expect(startMailType([member('receipt')])).toEqual({ value: 'receipt', mixed: false });
    expect(startMailType([member(null), member(null)])).toEqual({ value: '', mixed: false });
    expect(startMailType([member('receipt'), member(null)])).toEqual({ value: null, mixed: true });
  });

  it('puts mailType in the payload only when given', () => {
    const m = { ...member('receipt'), currentLabels: [], protected: false };
    const outcome = { topicLabel: 'Receipts', needsAction: false, toBeDeleted: false };
    expect(editRequest(m, outcome, undefined, undefined, 'newsletter').mailType).toBe('newsletter');
    expect(editRequest(m, outcome, undefined, undefined, '').mailType).toBe('');
    expect('mailType' in editRequest(m, outcome)).toBe(false);
  });
});
