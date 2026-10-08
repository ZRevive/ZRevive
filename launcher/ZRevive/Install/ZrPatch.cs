using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ZRevive.Launcher.Install;

public sealed class ZrPatchException : Exception
{
    public ZrPatchException(string message) : base(message) { }
}

/// <summary>
/// Applies a ZRPATCH "append" patch: the format deploy/assets/zrpatch.py produces.
/// <para>Why this exists: our releases change pack2 files that are hundreds of megabytes, and the
/// change is always "same body, new header, extra data on the end". An append patch therefore
/// carries only the new header plus the appended tail - a few megabytes instead of the whole pack -
/// and needs no diffing on the player's machine, just a copy and a concatenation.</para>
/// <para>It replaces the old "recipe" entries, which shipped Python scripts to run against the
/// player's install. That could never work: players have no Python. The scripts now run HERE, on
/// the build machine, and players get the result as a patch.</para>
/// <para>Layout (little-endian), 144-byte header then the replacement header bytes then the tail:
/// magic "ZRPATCH\x01", format(1)=APPEND, flags(1) bit0 = tail is zlib (raw deflate with a zlib
/// wrapper), reserved(2), headerLen(4), baseSize(8), resultSize(8), baseSha256(32), resultSha256(32),
/// prefixSha256(32), storedLen(8), tailLen(8).</para>
/// </summary>
public static class ZrPatch
{
    public const int HeaderSize = 144;
    static readonly byte[] Magic = "ZRPATCH\u0001"u8.ToArray();
    const byte FormatAppend = 1;
    const byte FlagZlib = 1;

    public sealed record Header(
        int HeaderLen, long BaseSize, long ResultSize,
        byte[] BaseSha256, byte[] ResultSha256, byte[] PrefixSha256,
        long StoredLen, long TailLen, bool Compressed);

    public static Header ReadHeader(string patchPath)
    {
        using var f = File.OpenRead(patchPath);
        var raw = new byte[HeaderSize];
        if (f.Read(raw, 0, HeaderSize) != HeaderSize)
            throw new ZrPatchException($"{Path.GetFileName(patchPath)} is too small to be a patch.");
        return ParseHeader(raw, Path.GetFileName(patchPath));
    }

    static Header ParseHeader(byte[] raw, string what)
    {
        for (var i = 0; i < Magic.Length; i++)
            if (raw[i] != Magic[i]) throw new ZrPatchException($"{what} is not a ZRevive patch file.");
        if (raw[8] != FormatAppend)
            throw new ZrPatchException($"{what} uses patch format {raw[8]}, which this launcher can't apply.");

        var flags = raw[9];
        var span = raw.AsSpan();
        var headerLen = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        var baseSize = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
        var resultSize = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
        var baseSha = span.Slice(32, 32).ToArray();
        var resultSha = span.Slice(64, 32).ToArray();
        var prefixSha = span.Slice(96, 32).ToArray();
        var storedLen = BinaryPrimitives.ReadInt64LittleEndian(span[128..]);
        var tailLen = BinaryPrimitives.ReadInt64LittleEndian(span[136..]);

        if (headerLen < 0 || baseSize < headerLen || resultSize < 0 || storedLen < 0 || tailLen < 0)
            throw new ZrPatchException($"{what} has a malformed header.");
        return new Header(headerLen, baseSize, resultSize, baseSha, resultSha, prefixSha,
            storedLen, tailLen, (flags & FlagZlib) != 0);
    }

    /// <summary>
    /// base + patch -> target, written to a temp file and renamed into place only after the result
    /// hashes to what the patch says it should. A half-written game file is never left behind.
    /// </summary>
    public static void Apply(string basePath, string patchPath, string targetPath, CancellationToken ct)
    {
        var h = ReadHeader(patchPath);

        var have = new FileInfo(basePath).Length;
        if (have != h.BaseSize)
            throw new ZrPatchException(
                $"{Path.GetFileName(basePath)} is {have:N0} bytes but this update expects {h.BaseSize:N0}. " +
                "The game file is not the build this release was made against.");

        if (!Sha256File(basePath, 0, h.BaseSize, ct).SequenceEqual(h.BaseSha256))
        {
            // Narrow it down the way the Python tool does: a body that still matches means the file
            // is already patched, which is a different (and harmless) situation from a corrupt one.
            if (Sha256File(basePath, h.HeaderLen, h.BaseSize - h.HeaderLen, ct).SequenceEqual(h.PrefixSha256))
                throw new ZrPatchException(
                    $"{Path.GetFileName(basePath)} is already patched, or was changed by other tooling.");
            throw new ZrPatchException(
                $"{Path.GetFileName(basePath)} does not match what this update was built against.");
        }

        byte[] replacementHeader;
        byte[] stored;
        using (var pf = File.OpenRead(patchPath))
        {
            pf.Position = HeaderSize;
            replacementHeader = ReadExactly(pf, h.HeaderLen, patchPath);
            stored = ReadExactly(pf, checked((int)h.StoredLen), patchPath);
        }

        var tail = h.Compressed ? Inflate(stored) : stored;
        if (tail.LongLength != h.TailLen)
            throw new ZrPatchException($"{Path.GetFileName(patchPath)} tail is {tail.LongLength} bytes, header says {h.TailLen}.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(targetPath) + ".zrpatch.tmp");
        try
        {
            using (var src = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                dst.Write(replacementHeader, 0, replacementHeader.Length);
                src.Position = h.HeaderLen;
                var left = h.BaseSize - h.HeaderLen;
                var buffer = new byte[1 << 20];
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = src.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                    if (n <= 0) throw new ZrPatchException($"{Path.GetFileName(basePath)}: unexpected end of file.");
                    dst.Write(buffer, 0, n);
                    left -= n;
                }
                dst.Write(tail, 0, tail.Length);
            }

            if (!Sha256File(tmp, 0, new FileInfo(tmp).Length, ct).SequenceEqual(h.ResultSha256))
                throw new ZrPatchException("the patched file is not what the update says it should be; nothing was installed.");

            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static byte[] ReadExactly(Stream s, int count, string what)
    {
        var buf = new byte[count];
        var done = 0;
        while (done < count)
        {
            var n = s.Read(buf, done, count - done);
            if (n <= 0) throw new ZrPatchException($"{Path.GetFileName(what)} is truncated.");
            done += n;
        }
        return buf;
    }

    static byte[] Inflate(byte[] stored)
    {
        using var input = new MemoryStream(stored);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    static byte[] Sha256File(string path, long offset, long length, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        f.Position = offset;
        var buffer = new byte[1 << 20];
        var left = length;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            var n = f.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
            if (n <= 0) break;
            sha.TransformBlock(buffer, 0, n, null, 0);
            left -= n;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }
}
