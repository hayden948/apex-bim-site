# Plugin logic tests

`TestMain.cs` exercises the non-Revit logic (unit conversion, AFIS/.pred.json
model binding, API URL validation). It was run against the reconstructed source
on .NET 8 — 30/30 assertions pass.

The Revit-dependent code paths (family geometry, parameters) can only be
exercised inside Revit; the flex-and-verify summary the M1 command shows after
each build is the in-Revit smoke test.
