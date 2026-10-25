using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaimCheck;

/// <summary>Thrown when a vault exists but cannot be decrypted (wrong/missing password,
/// DPAPI blob from another machine). Callers must treat this as fatal for that vault —
/// never fall through to an empty vault, which would be saved back over the real mapping.</summary>
public sealed class VaultCryptoException : Exception
{
    public VaultCryptoException(string message) : base(message) { }
}

/// <summary>
/// Vault at-rest encryption (v0.6.0). Modes:
///   - password: SANITIZE_VAULT_PASSWORD env set → PBKDF2-SHA256 (210k) → AES-GCM,
///     per-file random salt + nonce. Strongest: works on every OS, survives even
///     same-user compromise at rest. Forgotten password = mappings unrecoverable.
///   - dpapi: Windows default (no password env) → DPAPI CurrentUser, tied to the
///     Windows login. Zero friction, at-rest only.
///   - plaintext: Linux default without a password env — user-only file mode (0600)
///     plus a warning in ClaimCheck status. Auto-upgrades to password mode when
///     SANITIZE_VAULT_PASSWORD is set.
/// Migration is automatic: plaintext files are read as-is and re-written encrypted
/// on the next save; encrypted files decrypt on load.
/// </summary>
public static class VaultCrypto
{
    public const string PasswordEnvVar = "SANITIZE_VAULT_PASSWORD";
    private static readonly byte[] Magic = { 0x43, 0x43, 0x56, 0x45 }; // "CCVE"
    private const byte FormatVersion = 1;
    private const int TagLen = 16;
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly object KeyCacheLock = new();
    private static readonly Dictionary<string, byte[]> KeyCache = new();

    public static bool PasswordConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PasswordEnvVar));

    private static bool LooksEncrypted(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 6 && bytes[..4].SequenceEqual(Magic);

    public sealed record VaultInfo(string Protection, long? Entries, string? Error);

    /// <summary>True when the file carries the encrypted-vault header.</summary>
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            var read = fs.Read(head);
            return read == 4 && head.SequenceEqual(Magic);
        }
        catch { return false; }
    }

    /// <summary>Read + decrypt. Throws VaultCryptoException for undecryptable encrypted vaults
    /// (wrong/missing password, foreign DPAPI blob). Plaintext files return as-is.</summary>
    public static string ReadJson(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!LooksEncrypted(bytes)) return Encoding.UTF8.GetString(bytes);
        var (json, _) = Decrypt(bytes, path);
        return json;
    }

    /// <summary>Non-throwing variant for reporting/restore sweeps: locked or unreadable
    /// vaults return Error instead of the json.</summary>
    public static bool TryReadJson(string path, out string json, out string error)
    {
        try { json = ReadJson(path); error = null; return true; }
        catch (Exception e) { json = ""; error = e.Message; return false; }
    }

    /// <summary>Protection level + entry count for status output. Entries is null when
    /// the vault cannot be read (Error says why).</summary>
    public static VaultInfo Describe(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!LooksEncrypted(bytes))
            {
                long entries = 0;
                try { entries = JsonSerializer.Deserialize<VaultData>(Encoding.UTF8.GetString(bytes))?.Map.Count ?? 0; }
                catch { }
                return new VaultInfo("plaintext", entries, null);
            }
            var mode = bytes[5];
            var (json, protection) = Decrypt(bytes, path);
            long n = 0;
            try { n = JsonSerializer.Deserialize<VaultData>(json)?.Map.Count ?? 0; } catch { }
            return new VaultInfo(protection, n, null);
        }
        catch (VaultCryptoException e) { return new VaultInfo("locked", null, e.Message); }
        catch (Exception e) { return new VaultInfo("unreadable", null, e.Message); }
    }

    /// <summary>Serialize + write in the active mode: password (env set) → DPAPI (Windows)
    /// → plaintext + user-only file mode (Linux default).</summary>
    public static void WriteJson(string path, string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var pw = Environment.GetEnvironmentVariable(PasswordEnvVar);
        byte[] outBytes;
        if (!string.IsNullOrEmpty(pw))
        {
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            var key = DeriveKey(pw, salt, Pbkdf2Iterations);
            var body = AesGcmEncrypt(plain, key, nonce);
            outBytes = Build(ModePassword, salt, Pbkdf2Iterations, nonce, body);
        }
        else if (OperatingSystem.IsWindows())
        {
            outBytes = Build(ModeDpapi, null, null, null,
                ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        else
        {
            File.WriteAllBytes(path, plain); // Linux default: plaintext + caller applies 0600
            return;
        }
        File.WriteAllBytes(path, outBytes);
    }

    // ---- format ----

    private const byte ModeDpapi = 0;
    private const byte ModePassword = 1;

    private static byte[] Build(byte mode, byte[]? salt, int? iters, byte[]? nonce, byte[] body)
    {
        var head = new List<byte> { Magic[0], Magic[1], Magic[2], Magic[3], FormatVersion, mode };
        if (mode == ModePassword) { head.AddRange(salt!); head.AddRange(BitConverter.GetBytes(iters!.Value)); head.AddRange(nonce!); }
        head.AddRange(body);
        return head.ToArray();
    }

    private static (string Json, string Protection) Decrypt(byte[] bytes, string path)
    {
        var mode = bytes[5];
        if (mode == ModeDpapi)
        {
            if (!OperatingSystem.IsWindows())
                throw new VaultCryptoException("vault was encrypted with Windows DPAPI — decrypt it on the machine/user that encrypted it");
            var json = ProtectedData.Unprotect(bytes[6..], null, DataProtectionScope.CurrentUser);
            return (Encoding.UTF8.GetString(json), "DPAPI (Windows login)");
        }
        if (mode == ModePassword)
        {
            var pw = Environment.GetEnvironmentVariable(PasswordEnvVar);
            if (string.IsNullOrEmpty(pw))
                throw new VaultCryptoException($"vault is password-encrypted but {PasswordEnvVar} is not set");
            var salt = bytes[6..(6 + SaltLen)];
            var iters = BitConverter.ToInt32(bytes.AsSpan(6 + SaltLen, 4));
            var nonce = bytes.AsSpan(6 + SaltLen + 4, NonceLen).ToArray();
            var body = bytes[(6 + SaltLen + 4 + NonceLen)..];
            var key = DeriveKey(pw, salt, iters);
            var plain = AesGcmDecrypt(body, key, nonce);
            return (Encoding.UTF8.GetString(plain), "password");
        }
        throw new VaultCryptoException($"unknown vault encryption mode {mode}");
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        var cacheKey = Convert.ToHexString(salt) + ":" + iterations + ":" + password.Length;
        lock (KeyCacheLock)
        {
            if (KeyCache.TryGetValue(cacheKey, out var k)) return k;
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            KeyCache[cacheKey] = key;
            return key;
        }
    }

    private static byte[] AesGcmEncrypt(byte[] plain, byte[] key, byte[] nonce)
    {
        using var aes = new AesGcm(key, TagLen);
        var ct = new byte[plain.Length];
        var tag = new byte[TagLen];
        aes.Encrypt(nonce, plain, ct, tag);
        var outp = new byte[ct.Length + TagLen];
        Array.Copy(ct, outp, ct.Length);
        Array.Copy(tag, 0, outp, ct.Length, TagLen);
        return outp;
    }

    private static byte[] AesGcmDecrypt(byte[] ctAndTag, byte[] key, byte[] nonce)
    {
        using var aes = new AesGcm(key, TagLen);
        var ct = new byte[ctAndTag.Length - TagLen];
        var tag = new byte[TagLen];
        Array.Copy(ctAndTag, ct, ct.Length);
        Array.Copy(ctAndTag, ct.Length, tag, 0, TagLen);
        var plain = new byte[ct.Length];
        aes.Decrypt(nonce, ct, tag, plain);
        return plain;
    }
}
