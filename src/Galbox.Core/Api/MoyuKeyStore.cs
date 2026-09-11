using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Galbox.Core.Api;

/// <summary>
/// Stores the user's own <c>nmk_</c> API key.
///
/// <para>
/// <b>Bring your own key.</b> Galbox ships no key, so there is nothing to leak in the product
/// itself: a compromised key is one user's own, and its quota is spent against their own account.
/// </para>
///
/// <para>
/// The key is only ever read to be put on a request header, and is never written to a log, an
/// exception message, an acceptance report or a crash dump. Implementations must not put the value
/// in <see cref="object.ToString"/> results, and should expose
/// <see cref="FingerprintOf(string)"/> when something has to be shown.
/// </para>
/// </summary>
public interface IMoyuKeyStore
{
    /// <summary>True when a key is stored and readable.</summary>
    bool IsConfigured { get; }

    /// <summary>A short label for where keys come from, for diagnostics: <c>windows-dpapi</c>, <c>memory</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Returns the stored key, or <c>null</c> when none is configured <b>or</b> the store cannot be
    /// read. Callers must treat <c>null</c> as "not configured" and report it as such — never as an
    /// empty result set.
    /// </summary>
    string? Get();

    /// <summary>Stores (or, with <c>null</c>, clears) the key.</summary>
    void Set(string? apiKey);

    /// <summary>
    /// A one-way, shortened digest of a key, safe to display and to log. Two keys with the same
    /// fingerprint are the same key; the fingerprint cannot be turned back into the key.
    /// </summary>
    static string FingerprintOf(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return "(none)";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(digest.AsSpan(0, 6));
    }
}

/// <summary>
/// The DPAPI-backed store: the only one the application uses.
///
/// <para>
/// <b>What is written where.</b> A single binary file under
/// <c>%LocalAppData%\Galbox\secrets\</c>, containing a small magic header and then the ciphertext
/// produced by <see cref="ProtectedData.Protect(byte[], byte[]?, DataProtectionScope)"/> with
/// <see cref="DataProtectionScope.CurrentUser"/>. DPAPI derives the encryption key from the user's
/// Windows profile, so the file is useless when copied to another machine or read by another
/// account. It sits in its own folder, next to nothing else, and never touches
/// <c>galbox.db</c> — the user's library is not involved.
/// </para>
///
/// <para>
/// <b>Extra entropy.</b> The blob is additionally bound to a fixed application constant. DPAPI
/// would already refuse a different user; the entropy means another application running as the
/// <i>same</i> user cannot silently unprotect this file either.
/// </para>
///
/// <para>
/// <b>Failing closed.</b> A missing, truncated or undecryptable file yields <c>null</c> from
/// <see cref="Get"/> and <c>false</c> from <see cref="IsConfigured"/> — the client then reports
/// <see cref="MoyuFailureCode.NotConfigured"/> and sends nothing. It does not throw at startup, and
/// it never treats a broken store as "no patches exist".
/// </para>
/// </summary>
public sealed class MoyuDpapiKeyStore : IMoyuKeyStore
{
    /// <summary>
    /// Environment variable that relocates the store. Used by a developer and by the acceptance
    /// harness so a check can exercise this class without reading or writing the real key.
    /// </summary>
    public const string StorePathVariable = "GALBOX_MOYU_KEYSTORE";

    /// <summary>Application constant mixed into DPAPI as additional entropy.</summary>
    /// <remarks>
    /// Public so that an audit - an acceptance check, a security review - can prove the stored file
    /// really is DPAPI-protected under this entropy rather than merely "not obviously the key". It
    /// is a fixed, published constant, not a secret; the security comes from DPAPI binding the
    /// ciphertext to the current Windows user.
    /// </remarks>
    public static readonly byte[] AdditionalEntropy =
        Encoding.UTF8.GetBytes("Galbox.Moyu.nmk.v1:developer.nextmoe.dev");

    /// <summary>File magic, so a truncated or foreign file is recognised rather than decrypted blindly.</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GALBOX-MOYU-KEY");

    /// <summary>Container format version.</summary>
    private const byte FormatVersion = 1;

    /// <summary>
    /// Bytes before the ciphertext begins (magic + version). Public so an audit can locate the DPAPI
    /// blob inside the file and decrypt it by hand.
    /// </summary>
    public static int HeaderLength => Magic.Length + 1;

    private readonly string _storePath;

    /// <summary>Creates a store at the default location.</summary>
    public MoyuDpapiKeyStore()
        : this(ResolveDefaultStorePath())
    {
    }

    /// <summary>Creates a store backed by a specific file.</summary>
    public MoyuDpapiKeyStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = Path.GetFullPath(storePath);
    }

    /// <inheritdoc />
    public string Kind => "windows-dpapi";

    /// <summary>Absolute path of the file this store owns. Never contains a secret.</summary>
    public string StorePath => _storePath;

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(Get());

    /// <inheritdoc />
    public string? Get()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return null;
            }

            var raw = File.ReadAllBytes(_storePath);
            var offset = Magic.Length + 1;

            // Magic + version, then the ciphertext. Anything shorter is a foreign or damaged file.
            if (raw.Length <= offset)
            {
                return null;
            }

            if (!raw.AsSpan(0, Magic.Length).SequenceEqual(Magic) || raw[Magic.Length] != FormatVersion)
            {
                return null;
            }

            var ciphertext = raw[offset..];
            var plaintext = ProtectedData.Unprotect(ciphertext, AdditionalEntropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plaintext);

            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception)
        {
            // The store is unreadable. That is "not configured", which is a state the client must
            // handle anyway - and the user must not be shown a raw DPAPI exception.
            return null;
        }
    }

    /// <inheritdoc />
    public void Set(string? apiKey)
    {
        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Clear();
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[] ciphertext;
        try
        {
            ciphertext = ProtectedData.Protect(plaintext, AdditionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            // Best effort: this is a managed array, so it cannot be truly wiped, but the window
            // in which the key sits in a decrypted buffer stays as small as possible.
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var payload = new byte[Magic.Length + 1 + ciphertext.Length];
        Magic.CopyTo(payload, 0);
        payload[Magic.Length] = FormatVersion;
        ciphertext.CopyTo(payload, Magic.Length + 1);

        // Write to a sibling temp file and replace, so an interrupted write cannot leave a
        // half-encrypted store that would read back as "not configured".
        var temporary = _storePath + ".tmp";
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, _storePath, overwrite: true);
    }

    /// <summary>Removes the stored key. Safe to call when nothing is stored.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                File.Delete(_storePath);
            }
        }
        catch (Exception)
        {
            // A key that cannot be deleted is reported as configured on the next read, which is
            // the honest outcome; there is nothing useful to do with the exception here.
        }
    }

    /// <summary>
    /// The default store location: <c>%LocalAppData%\Galbox\secrets\moyu-api-key.bin</c>, or the
    /// path in <see cref="StorePathVariable"/> when that is set.
    /// </summary>
    public static string ResolveDefaultStorePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(StorePathVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "secrets",
            "moyu-api-key.bin");
    }

    /// <summary>
    /// A description of the store for a diagnostics log: the location and whether a key is present.
    /// Deliberately never the key.
    /// </summary>
    public override string ToString() =>
        $"MoyuDpapiKeyStore {{ Path = {_storePath}, Configured = {(File.Exists(_storePath) ? "yes" : "no")} }}";
}

/// <summary>
/// An in-memory key store. For tests, and for a caller that has the key in hand already.
/// </summary>
/// <remarks>
/// Registered nowhere in the shipping app — it exists so a check can drive the client with a known
/// key without touching the user's real store.
/// </remarks>
public sealed class MoyuMemoryKeyStore : IMoyuKeyStore
{
    private string? _key;

    /// <summary>Creates a store, optionally pre-loaded.</summary>
    public MoyuMemoryKeyStore(string? apiKey = null) => _key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    /// <inheritdoc />
    public string Kind => "memory";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(_key);

    /// <inheritdoc />
    public string? Get() => _key;

    /// <inheritdoc />
    public void Set(string? apiKey) => _key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    /// <inheritdoc />
    public override string ToString() => $"MoyuMemoryKeyStore {{ Configured = {IsConfigured} }}";
}
