using System.IO.Compression;
using System.Text;

namespace Galbox.PatchVerifier;

/// <summary>
/// Hand-rolled archive writer used by the verification harness.
/// <para>
/// The BCL zip writer normalises entry names and always writes them in one encoding, which makes it
/// impossible to build the hostile archives this harness needs (raw CP932 bytes without the UTF-8 flag,
/// <c>..\</c> traversal, absolute paths, unix symlink attributes). Writing the container by hand keeps
/// every byte under the harness's control, so the archives really are the "worst case" they claim to be.
/// </para>
/// </summary>
internal static class TestArchiveWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;

    internal sealed class Entry
    {
        public required string Name { get; init; }

        public byte[] Content { get; init; } = Array.Empty<byte>();

        /// <summary>Compression method: 0 = store, 8 = deflate.</summary>
        public ushort Method { get; init; }

        /// <summary>Raw name bytes. When null, <see cref="Name"/> is encoded as UTF-8.</summary>
        public byte[]? RawNameBytes { get; init; }

        /// <summary>Set the general purpose "UTF-8 file name" flag (bit 11).</summary>
        public bool Utf8Flag { get; init; }

        /// <summary>Unix mode written into the high 16 bits of the external attributes (0xA000 = symlink).</summary>
        public uint ExternalAttributes { get; init; }

        public bool IsDirectory { get; init; }
    }

    /// <summary>Writes a zip file. When <paramref name="cp932"/> is true the names are encoded as CP932 without the UTF-8 flag.</summary>
    public static void WriteZip(string path, IEnumerable<Entry> entries, bool cp932 = false)
    {
        var encoding = cp932
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var records = new List<(Entry Entry, byte[] NameBytes, uint Crc, long Offset, byte[] Payload)>();

        foreach (var entry in entries)
        {
            var nameBytes = entry.RawNameBytes ?? encoding.GetBytes(entry.Name);
            var payload = entry.Method == 8 ? Deflate(entry.Content) : entry.Content;
            var crc = Crc32(entry.Content);
            var offset = stream.Position;

            writer.Write(LocalHeaderSignature);
            writer.Write((ushort)20);
            writer.Write((ushort)(entry.Utf8Flag ? 0x0800 : 0));
            writer.Write(entry.Method);
            writer.Write((ushort)0x7C21); // fixed DOS time
            writer.Write((ushort)0x5A21); // fixed DOS date (2025-01-01)
            writer.Write(crc);
            writer.Write((uint)payload.Length);
            writer.Write((uint)entry.Content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Write(payload);

            records.Add((entry, nameBytes, crc, offset, payload));
        }

        var centralDirectoryOffset = stream.Position;
        foreach (var (entry, nameBytes, crc, offset, payload) in records)
        {
            writer.Write(CentralHeaderSignature);
            writer.Write((ushort)0x031E); // version made by: MS-DOS/Unix host 3
            writer.Write((ushort)20);
            writer.Write((ushort)(entry.Utf8Flag ? 0x0800 : 0));
            writer.Write(entry.Method);
            writer.Write((ushort)0x7C21);
            writer.Write((ushort)0x5A21);
            writer.Write(crc);
            writer.Write((uint)payload.Length);
            writer.Write((uint)entry.Content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(entry.ExternalAttributes);
            writer.Write((uint)offset);
            writer.Write(nameBytes);
        }

        var centralDirectorySize = stream.Position - centralDirectoryOffset;

        writer.Write(EndOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)records.Count);
        writer.Write((ushort)records.Count);
        writer.Write((uint)centralDirectorySize);
        writer.Write((uint)centralDirectoryOffset);
        writer.Write((ushort)0);
        writer.Flush();
    }

    /// <summary>Writes a gzipped tar file (exercises the SharpCompress code path).</summary>
    public static void WriteTarGz(string path, IEnumerable<(string Name, byte[] Content)> entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);

        foreach (var (name, content) in entries)
        {
            var header = new byte[512];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 100));
            WriteOctal(header, 100, 8, Convert.ToInt64("644", 8)); // mode
            WriteOctal(header, 108, 8, 0);              // uid
            WriteOctal(header, 116, 8, 0);              // gid
            WriteOctal(header, 124, 12, content.Length); // size
            WriteOctal(header, 136, 12, 0);             // mtime
            for (var i = 148; i < 156; i++) header[i] = (byte)' ';
            header[156] = (byte)'0';                    // regular file
            Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
            Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

            var checksum = 0;
            for (var i = 0; i < 512; i++) checksum += header[i];
            WriteOctal(header, 148, 8, checksum);

            gzip.Write(header);
            gzip.Write(content);

            var padding = (int)(512 - content.Length % 512) % 512;
            if (padding > 0) gzip.Write(new byte[padding]);
        }

        gzip.Write(new byte[1024]);
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            table[i] = value;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
