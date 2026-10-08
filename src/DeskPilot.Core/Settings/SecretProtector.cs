using System.Security.Cryptography;
using System.Text;

namespace DeskPilot.Core.Settings;

/// <summary>Encrypts API keys with Windows DPAPI for the current user. The ciphertext is useless on another account or PC.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeskPilot.ApiKey.v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    /// <summary>The key to use for a profile: the stored key, else the profile's environment variable, else "".</summary>
    public static string ResolveApiKey(ProviderProfile profile)
    {
        var stored = Unprotect(profile.ApiKeyProtected);
        if (!string.IsNullOrWhiteSpace(stored)) return stored.Trim();
        if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar))
        {
            var name = profile.ApiKeyEnvVar.Trim();
            // The user-level value too: a key saved with setx after DeskPilot started is not in our process env yet.
            var env = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(env))
            {
                try { env = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); }
                catch (System.Security.SecurityException) { env = null; }
            }
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        }
        return "";
    }

    /// <summary>"sk-ab…wxyz" style hint for the UI; never the full key.</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 8) return new string('•', key.Length);
        return key[..4] + "…" + key[^4..];
    }
}
