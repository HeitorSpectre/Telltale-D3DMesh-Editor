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
    private static readonly Dictionary<string, Dictionary<ulong, string>> FolderNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FolderNamesLock = new();
    private static readonly AsyncLocal<string?> CurrentTextureFolder = new();

    public static string Resolve(uint low, uint high)
    {
        var names = _names ??= HashNameDb.LoadBinary("TexNames.HashDB");
        var combined = ((ulong)high << 32) | low;
        if (names.TryGetValue(combined, out var name))
        {
            return StripD3dtxExtension(name);
        }
        if (TryResolveTftbE3TextureHash(combined, out name))
        {
            return name;
        }
        if (TryResolveFolderTextureHash(combined, out name))
        {
            return name;
        }

        var swapped = ((ulong)low << 32) | high;
        if (names.TryGetValue(swapped, out name))
        {
            return StripD3dtxExtension(name);
        }
        if (TryResolveTftbE3TextureHash(swapped, out name))
        {
            return name;
        }
        if (TryResolveFolderTextureHash(swapped, out name))
        {
            return name;
        }

        return $"0x{combined:X16}";
    }

    public static IDisposable UseTextureFolder(string? folder)
    {
        var previous = CurrentTextureFolder.Value;
        CurrentTextureFolder.Value = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? Path.GetFullPath(folder)
            : null;
        return new RestoreTextureFolder(previous);
    }

    private static bool TryResolveFolderTextureHash(ulong hash, out string name)
    {
        name = "";
        var folder = CurrentTextureFolder.Value;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var names = GetFolderTextureNames(folder);
        if (!names.TryGetValue(hash, out var resolvedName) || string.IsNullOrWhiteSpace(resolvedName))
        {
            return false;
        }

        name = resolvedName;
        return true;
    }

    private static Dictionary<ulong, string> GetFolderTextureNames(string folder)
    {
        folder = Path.GetFullPath(folder);
        lock (FolderNamesLock)
        {
            if (FolderNames.TryGetValue(folder, out var cached))
            {
                return cached;
            }

            var names = new Dictionary<ulong, string>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.d3dtx", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                var stem = StripD3dtxExtension(fileName);
                AddTextureName(names, fileName, stem);
                AddTextureName(names, stem, stem);
            }

            FolderNames[folder] = names;
            return names;
        }
    }

    private static void AddTextureName(Dictionary<ulong, string> names, string hashSource, string resolvedName)
    {
        if (string.IsNullOrWhiteSpace(hashSource) || string.IsNullOrWhiteSpace(resolvedName))
        {
            return;
        }

        var hash = Crc64Ecma.Compute(hashSource);
        names.TryAdd(hash, resolvedName);
    }

    private static bool TryResolveTftbE3TextureHash(ulong hash, out string name)
    {
        name = "";
        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3)
        {
            return false;
        }

        name = hash switch
        {
            0x45730782E0534ABEUL => "sk55_fiona_uprBodyAlt",
            0xD3A20FD1C5D29AF1UL => "sk55_fiona_bodyLower",
            0xC34B7F9E1167022CUL => "sk55_fiona_bodyLower_nm",
            _ => ""
        };
        return name.Length > 0;
    }

    private static string StripD3dtxExtension(string name)
    {
        return name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
            ? name[..^".d3dtx".Length]
            : name;
    }

    private sealed class RestoreTextureFolder(string? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentTextureFolder.Value = previous;
        }
    }
}
