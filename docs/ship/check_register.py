#!/usr/bin/env python3
"""Ship-readiness register checker (loop harness, V3 empirical gate).

Fails (exit 1) if:
  - any register table row's evidence cell is empty or lacks a LEDGER/session reference
  - any banned hedge word appears in a register or ledger status context
  - the ledger is missing a T0 line or the register references a cycle the ledger lacks
Usage: python3 docs/ship/check_register.py [register.md] [ledger.md]
"""
import re
import sys

BANNED = re.compile(r"\b(should work|appears to|presumably)\b", re.I)


def main() -> int:
    reg_path = sys.argv[1] if len(sys.argv) > 1 else "docs/ship/SHIP_READINESS.md"
    led_path = sys.argv[2] if len(sys.argv) > 2 else "docs/ship/LEDGER.md"
    reg = open(reg_path, encoding="utf-8").read()
    led = open(led_path, encoding="utf-8").read()
    errors = []

    if "T0 = " not in led:
        errors.append("LEDGER: no recorded T0 line")

    rows = [l for l in reg.splitlines() if l.startswith("| ") and l.count("|") >= 7]
    rows = [r for r in rows if not re.match(r"\|\s*#\s*\|", r) and "---" not in r]
    if len(rows) < 7:
        errors.append(f"REGISTER: only {len(rows)} data rows; minimum required coverage is 7")
    for r in rows:
        cells = [c.strip() for c in r.split("|")]
        num, evidence = cells[1], cells[4]
        if not evidence or not re.search(r"(Cycle|session|prior-round|LEDGER)", evidence, re.I):
            errors.append(f"REGISTER row {num}: evidence cell has no ledger/session reference: '{evidence[:60]}'")

    # The ban is on claims, not on quoted evidence: strip fenced code blocks first.
    strip_fences = lambda t: re.sub(r"```.*?```", "", t, flags=re.S)
    for name, text in (("REGISTER", strip_fences(reg)), ("LEDGER", strip_fences(led))):
        for m in BANNED.finditer(text):
            errors.append(f"{name}: banned hedge word '{m.group(0)}'")

    for cyc in set(re.findall(r"Cycle (\d+)", reg)):
        if f"Cycle {cyc}" not in led:
            errors.append(f"REGISTER references Cycle {cyc}, absent from LEDGER")

    required = ["schema integrity", "parser accuracy", "build success", "QA validation",
                "failure behavior", "licensing", "physically receives"]
    for req in required:
        if not re.search(req.replace(" ", r"[\s\S]{0,3}"), reg, re.I):
            errors.append(f"REGISTER: required coverage term missing: '{req}'")

    if errors:
        print("REGISTER CHECK FAILED:")
        for e in errors:
            print("  -", e)
        return 1
    print(f"REGISTER CHECK PASSED: {len(rows)} evidenced rows, T0 present, no banned words, coverage terms present.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
