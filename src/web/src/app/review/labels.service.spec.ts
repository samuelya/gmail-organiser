import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { LabelDto } from './labels.models';
import {
  buildLabelTree,
  filterLabelTree,
  labelPathError,
  labelPlacement,
  LabelsService,
} from './labels.service';

const labels: LabelDto[] = [
  { id: 'INBOX', name: 'INBOX', type: 'system' },
  { id: 'L1', name: 'Projects/Beta', type: 'user' },
  { id: 'L2', name: 'Projects', type: 'user' },
  { id: 'L3', name: 'Projects/Alpha/Invoices', type: 'user' },
  { id: 'L4', name: 'Archive', type: 'user' },
];

describe('label tree', () => {
  it('builds a sorted tree of user labels, marking parents no label has', () => {
    const tree = buildLabelTree(labels);
    expect(tree.map((n) => n.path)).toEqual(['Archive', 'Projects']);
    const projects = tree[1];
    expect(projects.exists).toBe(true);
    expect(projects.children.map((n) => n.name)).toEqual(['Alpha', 'Beta']);
    const alpha = projects.children[0];
    expect(alpha).toMatchObject({ path: 'Projects/Alpha', exists: false });
    expect(alpha.children[0]).toMatchObject({ path: 'Projects/Alpha/Invoices', exists: true });
  });

  it('filters by path, keeping ancestors of matches', () => {
    const tree = buildLabelTree(labels);
    const filtered = filterLabelTree(tree, ' invoice ');
    expect(filtered.map((n) => n.path)).toEqual(['Projects']);
    expect(filtered[0].children.map((n) => n.path)).toEqual(['Projects/Alpha']);
    expect(filterLabelTree(tree, '')).toHaveLength(2);
    expect(filterLabelTree(tree, 'nothing')).toEqual([]);
  });
});

describe('labelPathError', () => {
  it('accepts paths the API accepts', () => {
    expect(labelPathError('Projects/Alpha')).toBeNull();
    expect(labelPathError(' a/b/c/d/e ')).toBeNull();
    expect(labelPathError('x'.repeat(100))).toBeNull();
  });

  it.each([
    ['', 'Enter a label.'],
    ['a/b/c/d/e/f', 'At most five levels.'],
    ['a//b', "'/'-separated part"],
    ['a/ b', "'/'-separated part"],
    ['a/', "'/'-separated part"],
    ['x'.repeat(101), "'/'-separated part"],
    [Array(5).fill('x'.repeat(50)).join('/'), 'At most 225 characters.'],
    ['a\u0007b', 'No control characters.'],
    ['inbox', 'Gmail system label'],
    ['Sent Mail', 'Gmail system label'],
  ])('rejects %j', (path, message) => {
    expect(labelPathError(path)).toContain(message);
  });
});

describe('labelPlacement', () => {
  it('finds an existing label case-insensitively', () => {
    expect(labelPlacement('projects/beta', labels)).toEqual({
      exists: true,
      parent: 'projects/beta',
      created: [],
    });
  });

  it('places a new path under its deepest existing ancestor', () => {
    expect(labelPlacement('Projects/Gamma/2026', labels)).toEqual({
      exists: false,
      parent: 'Projects',
      created: ['Gamma', '2026'],
    });
    expect(labelPlacement('INBOX/New', labels)).toEqual({
      exists: false,
      parent: null,
      created: ['INBOX', 'New'],
    });
  });
});

describe('LabelsService', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    return { service: TestBed.inject(LabelsService), http: TestBed.inject(HttpTestingController) };
  }

  it('caches the labels until refreshed', async () => {
    const { service, http } = setup();
    const first = firstValueFrom(service.labels());
    http.expectOne('/api/labels').flush(labels);
    expect(await first).toEqual(labels);
    expect(await firstValueFrom(service.labels())).toEqual(labels);
    http.expectNone('/api/labels');

    const refreshed = firstValueFrom(service.refresh());
    const req = http.expectOne('/api/labels/refresh');
    expect(req.request.method).toBe('POST');
    req.flush([labels[1]]);
    await refreshed;
    expect(await firstValueFrom(service.labels())).toEqual([labels[1]]);
    http.verify();
  });

  it('retries after a failed load', async () => {
    const { service, http } = setup();
    const failed = firstValueFrom(service.labels()).catch(() => 'failed');
    http.expectOne('/api/labels').flush(null, { status: 503, statusText: 'Unavailable' });
    expect(await failed).toBe('failed');
    const retried = firstValueFrom(service.labels());
    http.expectOne('/api/labels').flush(labels);
    expect(await retried).toEqual(labels);
  });
});
