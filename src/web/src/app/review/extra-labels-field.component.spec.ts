import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { ExtraLabelsField } from './extra-labels-field.component';
import { LabelDto } from './labels.models';

const labels: LabelDto[] = [
  { id: 'INBOX', name: 'INBOX', type: 'system' },
  { id: 'L1', name: 'Projects', type: 'user' },
  { id: 'L2', name: 'Projects/Alpha', type: 'user' },
  { id: 'L3', name: 'Receipts', type: 'user' },
  { id: 'L4', name: 'Travel', type: 'user' },
];

@Component({
  imports: [ExtraLabelsField, ReactiveFormsModule],
  template: `<app-extra-labels-field
    [formControl]="control"
    [labels]="labels()"
    [exclude]="exclude()"
    [max]="max()"
    [serverError]="serverError()"
  />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly control = new FormControl<string[]>([], { nonNullable: true });
  readonly labels = signal<LabelDto[] | null>(labels);
  readonly exclude = signal<(string | null)[]>(['Receipts', 'Act']);
  readonly max = signal(3);
  readonly serverError = signal<string | null>(null);
}

describe('ExtraLabelsField', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  function setup(value: string[] = []) {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    const fixture = TestBed.createComponent(Host);
    const host = fixture.componentInstance;
    host.control.setValue(value);
    fixture.autoDetectChanges();
    TestBed.tick();
    return { fixture, host };
  }

  const q = <T extends HTMLElement>(id: string) =>
    document.querySelector<T>(`[data-testid="${id}"]`);
  const all = (id: string) =>
    Array.from(document.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
  const chips = () =>
    all('extra-label-chip').map((c) => c.textContent!.replace('cancel', '').trim());
  const input = () => q<HTMLInputElement>('extra-labels-input')!;
  const type = (value: string) => {
    const el = input();
    el.focus();
    el.value = value;
    el.dispatchEvent(new Event('input'));
    TestBed.tick();
  };
  const key = (k: string, keyCode: number) => {
    input().dispatchEvent(new KeyboardEvent('keydown', { key: k, keyCode, bubbles: true }));
    TestBed.tick();
  };
  const enter = () => key('Enter', 13);

  it('shows the value as chips and marks paths Gmail does not have as new', () => {
    setup(['Travel', 'Ideas/Later']);
    expect(chips()).toEqual(['Travel', 'Ideas/Later new']);
    expect(all('extra-label-new')).toHaveLength(1);
  });

  it('adds a typed path on Enter or comma, using the existing spelling', () => {
    const { host } = setup();
    type('travel');
    enter();
    type('Ideas/Later');
    key(',', 188);
    expect(host.control.value).toEqual(['Travel', 'Ideas/Later']);
    expect(host.control.dirty).toBe(true);
    expect(input().value).toBe('');
  });

  it('offers user labels not chosen nor excluded that match the text, and adds the picked one', () => {
    const { host } = setup(['Projects']);
    type('');
    const options = () => all('extra-label-option').map((o) => o.textContent!.trim());
    expect(options()).toEqual(['Projects/Alpha', 'Travel']);
    type('alp');
    expect(options()).toEqual(['Projects/Alpha']);
    all('extra-label-option')[0].click();
    TestBed.tick();
    expect(host.control.value).toEqual(['Projects', 'Projects/Alpha']);
  });

  it('refuses blank, duplicate, excluded and malformed entries with a message', () => {
    const { host } = setup(['Travel']);
    type('   ');
    enter();
    expect(q('extra-labels-error')).toBeNull();
    type('TRAVEL');
    enter();
    expect(q('extra-labels-error')!.textContent).toContain("'Travel' is already added.");
    type('receipts');
    enter();
    expect(q('extra-labels-error')!.textContent).toContain('already the topic');
    type('a//b');
    enter();
    expect(q('extra-labels-error')!.textContent).toContain("'/'-separated part");
    expect(host.control.value).toEqual(['Travel']);
    type('a/b');
    expect(q('extra-labels-error')).toBeNull();
  });

  it('disables the input with a hint at the limit and removes a chip', () => {
    const { host } = setup(['Travel', 'Projects', 'Ideas']);
    expect(input().disabled).toBe(true);
    expect(q('extra-labels-full')!.textContent).toContain('At most 3 additional labels');
    all('extra-label-remove')[1].click();
    TestBed.tick();
    expect(host.control.value).toEqual(['Travel', 'Ideas']);
    expect(input().disabled).toBe(false);
  });

  it('makes the control invalid while a chip equals an excluded name', () => {
    const { host } = setup(['Travel']);
    expect(host.control.valid).toBe(true);
    host.exclude.set(['travel']);
    TestBed.tick();
    expect(host.control.errors?.['extraLabels']).toContain("'Travel'");
    expect(q('extra-labels-error')).not.toBeNull();
    all('extra-label-remove')[0].click();
    TestBed.tick();
    expect(host.control.valid).toBe(true);
    expect(host.control.value).toEqual([]);
  });

  it('shows a server error until the value changes', () => {
    const { host } = setup(['Travel']);
    host.serverError.set("Entry 1 'Travel': not allowed.");
    TestBed.tick();
    expect(q('extra-labels-error')!.textContent).toContain('not allowed');
  });

  it('follows the control being disabled', () => {
    const { host } = setup(['Travel']);
    host.control.disable();
    TestBed.tick();
    expect(input().disabled).toBe(true);
  });
});
