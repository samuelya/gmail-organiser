Closes #

## Summary
<!-- what changed and why, three lines at most -->

## Design
<!-- new or changed classes/components and their single responsibility; abstractions added and why -->

## Coder self-check
<!-- Tick only what you verified on this head; scale to the diff. A ticked row the tester finds broken is a bug and a failed round. -->
- [ ] Every acceptance criterion in the issue
- [ ] Tests use the fake Gmail / fake LLM and synthetic data only
- [ ] Nothing hardcoded or personal (labels, models, emails, URLs, IDs)
- [ ] Gmail mutations: no permanent delete, undo log written (when touched)
- [ ] Migration applies on an existing DB; jobs resume after restart (when data or jobs changed)
- [ ] 1280×800 and 390×844, keyboard use, no console errors (web)
- [ ] Build and lint clean; tests for the touched code green locally

## Owner check
<!-- what only the real mailbox, Google OAuth, Ollama models or Claude Desktop can show; "none" if nothing -->

## How to test
