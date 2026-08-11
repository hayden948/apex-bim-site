using System;

namespace Apex.BimStudio;

/// <summary>
/// Length-unit conversion to Revit internal units (decimal feet).
/// Central place for the conversions that were previously hardcoded
/// (inches-only in the .pred.json path, meters-only in the AFIS path).
/// </summary>
public static class UnitConv
{
    public const double MetersToFeet = 3.280839895;
    public const double InchesPerFoot = 12.0;

    /// <summary>
    /// Convert a length to feet given a declared unit string.
    /// Falls back to <paramref name="defaultUnit"/> when the value carries no unit,
    /// which preserves the historical behavior of each ingestion path
    /// (.pred.json defaulted to inches, AFIS to meters).
    /// </summary>
    public static double ToFeet(double value, string? unit, string defaultUnit)
    {
        string u = string.IsNullOrWhiteSpace(unit) ? defaultUnit : unit!.Trim().ToLowerInvariant();
        switch (u)
        {
            case "ft":
            case "feet":
            case "foot":
                return value;
            case "in":
            case "inch":
            case "inches":
            case "\"":
                return value / InchesPerFoot;
            case "mm":
            case "millimeter":
            case "millimeters":
                return value / 1000.0 * MetersToFeet;
            case "cm":
            case "centimeter":
            case "centimeters":
                return value / 100.0 * MetersToFeet;
            case "m":
            case "meter":
            case "meters":
                return value * MetersToFeet;
            default:
                ApexLog.Warn($"Unknown length unit '{unit}', assuming {defaultUnit}.");
                return ToFeet(value, defaultUnit, defaultUnit);
        }
    }
}
