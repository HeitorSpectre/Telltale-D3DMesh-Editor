using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace TelltaleD3DMeshEditor.Core;

// Loader for the bundled Telltale hash databases (BoneNames/TexNames).
// Files are copied to the "HashDBs" folder next to the .exe (see .csproj / Resources\HashDBs),
// so the app stays offline and self-contained without depending on external folders.
public static class HashNameDb
{
    // Single local folder: <exe directory>\HashDBs. Kept as a property for straightforward validation.
    public static string HashDbFolder => Path.Combine(AppContext.BaseDirectory, "HashDBs");

    public static string? Find(string fileName)
    {
        var path = Path.Combine(HashDbFolder, fileName);
        return File.Exists(path) ? path : null;
    }

    // .HashDB format: uint32 count, then [uint64 little-endian hash][null-terminated ASCII name].
    public static Dictionary<ulong, string> LoadBinary(string fileName)
    {
        var result = new Dictionary<ulong, string>();
        var path = Find(fileName);
        if (path is null || new FileInfo(path).Length < 4)
        {
            return result;
        }

        var data = File.ReadAllBytes(path);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        var offset = 4;
        for (var i = 0; i < count && offset + 8 <= data.Length; i++)
        {
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
            offset += 8;
            var start = offset;
            while (offset < data.Length && data[offset] != 0)
            {
                offset++;
            }
            result[hash] = Encoding.ASCII.GetString(data, start, offset - start);
            if (offset < data.Length)
            {
                offset++;
            }
        }

        return result;
    }
}

// Resolves bone hashes (low/high 32 bits) to readable names via BoneNames.HashDB.
public static class BoneHashDatabase
{
    private static Dictionary<ulong, string>? _names;

    public static string? Resolve(ulong hash)
    {
        var names = _names ??= HashNameDb.LoadBinary("BoneNames.HashDB");
        return names.TryGetValue(hash, out var name) ? name : null;
    }
}

// Resolves texture hashes (low/high 32 bits) to readable names via TexNames.HashDB.
public static class TextureHashDatabase
{
    private static Dictionary<ulong, string>? _names;

    public static string Resolve(uint low, uint high)
    {
        var names = _names ??= HashNameDb.LoadBinary("TexNames.HashDB");
        var combined = ((ulong)high << 32) | low;
        if (names.TryGetValue(combined, out var name))
        {
            return StripD3dtxExtension(name);
        }

        var swapped = ((ulong)low << 32) | high;
        return names.TryGetValue(swapped, out name)
            ? StripD3dtxExtension(name)
            : $"0x{combined:X16}";
    }

    private static string StripD3dtxExtension(string name)
    {
        return name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
            ? name[..^".d3dtx".Length]
            : name;
    }
}
