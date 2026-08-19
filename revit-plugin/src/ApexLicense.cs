using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Apex.BimStudio;

/// <summary>
/// Round 5: offline Ed25519 license check. No network, no phone-home — a
/// signed text file the customer keeps on the machine.
///
/// File format (license.apexlic): "&lt;base64(payload-json)&gt;.&lt;base64(signature)&gt;"
/// where payload is {"licensee","product","issued_utc","expires_utc"} and the
/// signature is Ed25519 over the exact payload bytes (no canonicalization
/// games — the signed bytes ARE the encoded payload). The PRIVATE key never
/// ships and is not in this repository (the repo is public); the public key
/// below can verify but cannot mint.
///
/// Fail-safe rules: this class never throws out of Check/Verify — any parse,
/// crypto, or IO surprise is an Invalid/Missing status with a message in the
/// customer's language. A licensing bug must never take Revit down.
/// </summary>
public static class ApexLicense
{
    public enum State { Valid, Missing, Expired, Invalid }

    public sealed class Status
    {
        public State State;
        public string? Licensee;
        public DateTime? ExpiresUtc;
        public string Message = "";
    }

    public const string Product = "ApexBimStudio";
    public const string FileName = "license.apexlic";

    /// <summary>
    /// Production verification key (Ed25519 public key, base64). Generated
    /// round 5; the signing half lives in the operator's service-role-only
    /// config store, never in this repository.
    /// </summary>
    public const string ProductionPublicKeyB64 = "pX50xlzPxRqI2npgwbuxnYUFyetR7NH4tPt1oC9sgZM=";

    private static Status? _cached;

    /// <summary>
    /// Session check used by the commands: looks for the license in
    /// %ProgramData%\Apex\license.apexlic, then next to the add-in DLL.
    /// Result is cached for the Revit session (drop the file in and restart
    /// Revit — stated in the message). Never throws.
    /// </summary>
    public static Status CheckDefault()
    {
        if (_cached != null) return _cached;
        Status s;
        try
        {
            string programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Apex", FileName);
            string beside = Path.Combine(
                Path.GetDirectoryName(typeof(ApexLicense).Assembly.Location) ?? ".", FileName);
            string? found = File.Exists(programData) ? programData
                : File.Exists(beside) ? beside : null;
            if (found == null)
            {
                s = new Status
                {
                    State = State.Missing,
                    Message = "No license file was found.\n\nPut the license.apexlic file you " +
                        $"received from Apex into:\n{Path.GetDirectoryName(programData)}\n\n" +
                        "then restart Revit. If you don't have the file, contact Apex.",
                };
            }
            else
            {
                s = Verify(File.ReadAllText(found), Convert.FromBase64String(ProductionPublicKeyB64),
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            ApexLog.Error("License check failed unexpectedly.", ex);
            s = new Status
            {
                State = State.Invalid,
                Message = "The license could not be checked (" + ex.Message + "). " +
                    "Contact Apex with the Apex log file.",
            };
        }
        ApexLog.Info($"License: {s.State}" +
            (s.Licensee != null ? $" — {s.Licensee}, expires {s.ExpiresUtc:yyyy-MM-dd}" : ""));
        _cached = s;
        return s;
    }

    /// <summary>Test hook: forget the cached session status.</summary>
    public static void ResetCache() => _cached = null;

    /// <summary>Verify one license text against a verification key. Never throws.</summary>
    public static Status Verify(string licenseText, byte[] publicKey, DateTime nowUtc)
    {
        const string damaged =
            "The license file is damaged or was altered — its signature does not match.\n" +
            "Re-copy the original license.apexlic you received from Apex; if it still fails, " +
            "contact Apex for a fresh copy.";
        try
        {
            string[] parts = (licenseText ?? "").Trim().Split('.');
            if (parts.Length != 2)
                return new Status { State = State.Invalid, Message = damaged };
            byte[] payload, sig;
            try
            {
                payload = Convert.FromBase64String(parts[0].Trim());
                sig = Convert.FromBase64String(parts[1].Trim());
            }
            catch (FormatException)
            {
                return new Status { State = State.Invalid, Message = damaged };
            }

            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            signer.BlockUpdate(payload, 0, payload.Length);
            if (!signer.VerifySignature(sig))
                return new Status { State = State.Invalid, Message = damaged };

            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            JsonElement root = doc.RootElement;
            string? product = root.TryGetProperty("product", out JsonElement p) ? p.GetString() : null;
            if (product != Product)
                return new Status
                {
                    State = State.Invalid,
                    Message = "This license is for a different Apex product" +
                        (product != null ? $" ('{product}')" : "") + ", not the Revit add-in. " +
                        "Contact Apex for the correct file.",
                };
            string? licensee = root.TryGetProperty("licensee", out JsonElement l) ? l.GetString() : null;
            if (!root.TryGetProperty("expires_utc", out JsonElement e)
                || !DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime expires))
                return new Status { State = State.Invalid, Message = damaged };

            if (nowUtc > expires)
                return new Status
                {
                    State = State.Expired,
                    Licensee = licensee,
                    ExpiresUtc = expires,
                    Message = $"The Apex license for {licensee ?? "this machine"} expired on " +
                        $"{expires:yyyy-MM-dd}.\n\nContact Apex to renew; a new license.apexlic " +
                        "file replaces this one, no reinstall needed.",
                };

            return new Status
            {
                State = State.Valid,
                Licensee = licensee,
                ExpiresUtc = expires,
                Message = $"Licensed to {licensee ?? "(unnamed)"} until {expires:yyyy-MM-dd}.",
            };
        }
        catch (Exception ex)
        {
            return new Status
            {
                State = State.Invalid,
                Message = damaged + $"\n\n(Technical detail for support: {ex.Message})",
            };
        }
    }
}
