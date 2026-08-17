# Ship-readiness loop protocol

Every ship-readiness round reads this file and `docs/ship/LEDGER.md` on start.

## LOOP PROTOCOL

1. Run `date` and record T0 in LEDGER.md. Hard stop at T0+120min. Re-run `date` at every checkpoint;
   never estimate elapsed time from memory.
2. Work in 25-minute cycles. At the end of each cycle append to LEDGER.md:
   cycle number, wall-clock, what changed (file paths), what is PROVEN, what is ASSUMED, next action.
3. RE-EVALUATION at the end of cycles 2 and 4: re-read the round's GOAL and EXIT block, then answer
   in writing — (a) is the current line of work still the highest-value path to EXIT? (b) what did I
   learn that invalidates the plan I started with? (c) what am I avoiding because it is hard?
   If (a) is "no", change course and log the pivot. Sunk effort is not a reason to continue.
4. LEDGER.md is the source of truth, not your context window. Write to it as if the session will be
   killed and resumed by a fresh agent with zero memory — because it may be.
4b. If your context was compacted, or you are at all unsure of prior state, STOP and re-read
   LEDGER.md in full before your next action. Never reconstruct state from memory. Long loops
   degrade around the 45–60 minute mark; the ledger is the recovery mechanism, but only if you
   actually re-read it.
5. Never mark anything DONE without pasted command output in the ledger. "Should work", "appears to",
   and "presumably" are banned words in status lines.

## TRIPLE VERIFICATION (required before any EXIT claim; all three must be independent)

V1 SELF: re-read your own diff line by line against the acceptance criteria. Produce an explicit list
   of every claim you cannot back with output. Assume you were sloppy and go find where.
V2 ADVERSARIAL: spawn a read-only reviewer subagent from .claude/agents/ whose instruction is to
   REFUTE the claim, not confirm it — give it ONLY the acceptance criteria and the diff. Do NOT
   pass it your conclusions, your reasoning, or your V1 findings; a reviewer that reads your
   verdict first is a rubber stamp. Tell it to find the input that breaks this. Verdict must be
   REFUTED or SURVIVED with a reason. If your own V1 and the reviewer agree on everything, be
   suspicious: you probably framed the question to produce agreement. Re-run with a harsher framing.
V3 EMPIRICAL: build and execute. Paste real terminal output, real file paths, real counts, real
   exit codes. A passing test that never could have failed is not evidence — show the test failing
   when you break the input.
Log all three verdicts per claim in LEDGER.md. Any claim that fails one gate is NOT green.

## STOP CONDITIONS — stop and write a HANDOFF block instead of pushing on:

- the same failure recurs 3 times with different fixes
- a fix requires changing the FamilySpec schema outside a round that authorizes it
- you are about to delete or rewrite something you did not read in full
- T0+120min

## Environment note (recorded round 1)

These rounds run in a Linux container against the GitHub repos, not on the operator's
Windows machine at `C:\Apex\Product & Engineering`. Consequences every round must respect:

- `hayden948/apex-bim-site` is cloned at `/home/user/apex-bim-site` (plugin, Supabase API, console).
- `hayden948/apex-parser-service` is cloned at `/workspace/apex-parser-service` (parser side).
- CLAUDE.md / SPRINT.md / AGENTS.md from the Windows folder are NOT in either repo — if they hold
  round-scoping decisions, they must be pushed or pasted before they can bind these rounds.
- Revit itself cannot run here (Linux, no Revit install, no license). Any "build and execute"
  verification of Revit-API code paths is limited to compiling both targets and running the
  RevitStub-based test suite. Journal playback / Design Automation runs require the operator's
  machine or a cloud DA setup — see the harness-check row of the gap register.
