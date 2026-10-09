using System.Security.Cryptography;
using System.Text;

namespace DeskPilot.Core.Settings;

/// <summary>
/// Encrypts API keys at rest. Windows: DPAPI for the current user (useless on another account or PC).
/// Linux and others: AES-GCM with a random 256-bit key kept in a file only the user can read
/// (<c>~/.config/DeskPilot/.secret-key</c>, mode 0600), the same protection level as other desktop apps
/// that do not use a keyring. Non-Windows ciphertext is prefixed "aesgcm:".
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeskPilot.ApiKey.v1");
    private const string AesPrefix = "aesgcm:";
    private static readonly object KeyGate = new();
    private static byte[]? _fileKey;

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(FileKey(), tag.Length)) aes.Encrypt(nonce, data, cipher, tag, Entropy);
        return AesPrefix + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            if (protectedBase64.StartsWith(AesPrefix, StringComparison.Ordinal))
            {
                var blob = Convert.FromBase64String(protectedBase64[AesPrefix.Length..]);
                int n = AesGcm.NonceByteSizes.MaxSize, t = AesGcm.TagByteSizes.MaxSize;
                if (blob.Length < n + t) return "";
                var plain = new byte[blob.Length - n - t];
                using (var aes = new AesGcm(FileKey(), t))
                    aes.Decrypt(blob.AsSpan(0, n), blob.AsSpan(n + t), blob.AsSpan(n, t), plain, Entropy);
                return Encoding.UTF8.GetString(plain);
            }
            if (!OperatingSystem.IsWindows()) return "";
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The per-user key for non-Windows systems, created on first use with owner-only permissions.</summary>
    private static byte[] FileKey()
    {
        lock (KeyGate)
        {
            if (_fileKey != null) return _fileKey;
            var path = Path.Combine(AppPaths.DataRoot, ".secret-key");
            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length == 32) return _fileKey = existing;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = RandomNumberGenerator.GetBytes(32);
            var tmp = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            // Owner-only from creation, so the key is never readable by others, not even briefly.
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(Convert.ToBase64String(key));
            File.Move(tmp, path, overwrite: true);
            return _fileKey = key;
        }
    }

    /// <summary>Tests only: forget the cached key (AppPaths may have been redirected).</summary>
    internal static void ResetKeyCacheForTests()
    {
        lock (KeyGate) _fileKey = null;
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
