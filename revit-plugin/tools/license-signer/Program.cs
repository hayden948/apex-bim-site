using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

// Apex license signer (operator tool — NEVER ships to customers).
//
//   keygen                                  -> prints private/public key (base64)
//   sign <privB64> <licensee> <expiresIso>  -> prints a license.apexlic body
//
// The private key belongs in the service-role-only config store (app_config),
// never in the repository (the repo is public) and never on a customer machine.
// Build: dotnet run in this folder (BouncyCastle.Cryptography from NuGet), or
// compile Program.cs against BouncyCastle.Cryptography.dll with csc.
static class Signer
{
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "keygen")
        {
            byte[] seed = new byte[32];
            RandomNumberGenerator.Fill(seed);
            var priv = new Ed25519PrivateKeyParameters(seed, 0);
            byte[] pub = priv.GeneratePublicKey().GetEncoded();
            Console.WriteLine("private_b64=" + Convert.ToBase64String(seed));
            Console.WriteLine("public_b64=" + Convert.ToBase64String(pub));
            return 0;
        }
        if (args.Length == 3 && args[0] == "verify")
        {
            // verify <pubB64> <licenseFile> — re-check any issued license against
            // any public key (e.g. the app_config production licenses against the
            // key embedded in the shipped add-in) without touching Revit.
            string text = System.IO.File.ReadAllText(args[2]).Trim();
            string[] parts = text.Split('.');
            if (parts.Length != 2) { Console.WriteLine("INVALID: not payload.signature"); return 1; }
            byte[] payload = Convert.FromBase64String(parts[0]);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(args[1]), 0));
            verifier.BlockUpdate(payload, 0, payload.Length);
            bool ok = verifier.VerifySignature(Convert.FromBase64String(parts[1]));
            Console.WriteLine((ok ? "SIGNATURE OK: " : "SIGNATURE FAIL: ") + Encoding.UTF8.GetString(payload));
            return ok ? 0 : 1;
        }
        if (args.Length == 4 && args[0] == "sign")
        {
            var priv = new Ed25519PrivateKeyParameters(Convert.FromBase64String(args[1]), 0);
            string payloadJson = JsonSerializer.Serialize(new
            {
                licensee = args[2],
                product = "ApexBimStudio",
                issued_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                expires_utc = args[3],
            });
            byte[] payload = Encoding.UTF8.GetBytes(payloadJson);
            var signer = new Ed25519Signer();
            signer.Init(true, priv);
            signer.BlockUpdate(payload, 0, payload.Length);
            byte[] sig = signer.GenerateSignature();
            Console.WriteLine(Convert.ToBase64String(payload) + "." + Convert.ToBase64String(sig));
            return 0;
        }
        Console.Error.WriteLine("usage: keygen | sign <privB64> <licensee> <expiresIsoUtc> | verify <pubB64> <licenseFile>");
        return 2;
    }
}
