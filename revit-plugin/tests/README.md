# Plugin logic tests

`TestMain.cs` exercises the non-Revit logic (unit conversion, AFIS/.pred.json
model binding, API URL validation). It runs in CI on every push
(`dotnet run --project revit-plugin/tests`) and locally the same way —
33/33 assertions pass.

The Revit-dependent code paths (family geometry, parameters) can only be
exercised inside Revit; the flex-and-verify summary the M1 command shows after
each build is the in-Revit smoke test.
