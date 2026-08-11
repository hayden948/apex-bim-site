using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Apex.BimStudio;

/// <summary>
/// Stores the Apex access token encrypted with Windows DPAPI (per-user scope)
/// under %LOCALAPPDATA%\Apex\token.bin. Replaces the previous in-memory-only,
/// plain-string token property.
/// </summary>
public static class TokenStore
{
    private static string TokenPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Apex", "token.bin");
        }
    }

    public static void Save(string accessToken)
    {
        try
        {
            byte[] plain = Encoding.UTF8.GetBytes(accessToken);
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllBytes(TokenPath, cipher);
        }
        catch (Exception ex)
        {
            ApexLog.Error("Failed to persist access token.", ex);
        }
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            byte[] cipher = File.ReadAllBytes(TokenPath);
            byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            ApexLog.Error("Failed to load stored access token; treating as signed out.", ex);
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(TokenPath)) File.Delete(TokenPath);
        }
        catch (Exception ex)
        {
            ApexLog.Error("Failed to clear stored access token.", ex);
        }
    }
}
