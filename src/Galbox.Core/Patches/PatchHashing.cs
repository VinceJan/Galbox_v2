using System.Buffers;
using System.Security.Cryptography;

namespace Galbox.Core.Patches;

/// <summary>
/// Hashing helpers. The engine only ever uses SHA-256 so that "same bytes" claims are
/// reproducible by the user with any off-the-shelf tool (<c>certutil -hashfile x sha256</c>).
/// </summary>
public static class PatchHashing
{
    /// <summary>Name of the algorithm, stored inside manifests/previews for self-description.</summary>
    public const string Algorithm = "sha256";

    private const int BufferSize = 1024 * 128;

    /// <summary>Computes the SHA-256 of a file, returned as lowercase hex. Returns null when the file does not exist.</summary>
    public static async Task<string?> TryHashFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(LongPath.Ensure(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Computes the SHA-256 of a file, throwing when it is missing.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
        => await TryHashFileAsync(path, ct).ConfigureAwait(false)
           ?? throw new FileNotFoundException($"Cannot hash '{path}': file not found.", path);

    /// <summary>Computes the SHA-256 of a stream from its current position.</summary>
    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hashes a byte array (used for tiny synthetic payloads).</summary>
    public static string HashBytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Copies a stream to a target file and returns (bytesWritten, sha256) in a single pass.</summary>
    public static async Task<(long Bytes, string Hash)> CopyAndHashAsync(Stream source, string targetPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LongPath.Ensure(Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Target '{targetPath}' has no directory part.")));

        using var sha = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            await using var target = new FileStream(LongPath.Ensure(targetPath), FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            await target.FlushAsync(ct).ConfigureAwait(false);
            return (total, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Verifies that a file on disk matches an expected SHA-256 (used for read-back checks).</summary>
    public static async Task<bool> VerifyFileAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        var actual = await TryHashFileAsync(path, ct).ConfigureAwait(false);
        return actual is not null && string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
