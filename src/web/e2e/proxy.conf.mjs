// The dev-server proxy (`proxy.conf.json`) with its API target taken from `E2E_API_URL`, so the
// smoke spec can run against an API on another port. Defaults to the same target as `npm start`.
import { readFileSync } from 'node:fs';

const base = JSON.parse(readFileSync(new URL('../proxy.conf.json', import.meta.url), 'utf8'));
const target = process.env.E2E_API_URL;

export default target
  ? Object.fromEntries(Object.entries(base).map(([path, entry]) => [path, { ...entry, target }]))
  : base;
