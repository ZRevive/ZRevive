using System.IO;
using System.Security.Cryptography;

namespace ZRevive.Launcher.Install;

public static class FileHasher
{
    const int Buffer = 1 << 20; // 1 MiB: the pack2 files are gigabytes

    public static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Constant-time-ish, case-insensitive hex compare that tolerates null.</summary>
    public static bool HashesEqual(string? a, string? b)
    {
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var x = char.ToLowerInvariant(a[i]);
            var y = char.ToLowerInvariant(b[i]);
            diff |= x ^ y;
        }
        return diff == 0;
    }

    /// <summary>
    /// SHA-256 of a file. The source is opened read-only / share-read so hashing can never
    /// modify the player's Steam install.
    /// </summary>
    public static async Task<string> HashFileAsync(string path, IProgress<long>? read = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buf = new byte[Buffer];
        long total = 0;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.TransformBlock(buf, 0, n, null, 0);
            total += n;
            read?.Report(total);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return ToHex(sha.Hash!);
    }

    /// <summary>
    /// Cheap-first verification: a size mismatch rules the file out without reading it.
    /// Pass expectedSize &lt; 0 to skip the size check.
    /// </summary>
    public static async Task<bool> VerifyAsync(string path, string expectedSha256, long expectedSize,
        IProgress<long>? read = null, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (expectedSize >= 0 && info.Length != expectedSize) return false;
        return HashesEqual(await HashFileAsync(path, read, ct), expectedSha256);
    }
}
