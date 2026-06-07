namespace TelltaleD3DMeshEditor.Core;

// Telltale symbol hash = CRC64-ECMA182 of the lowercase name. Used to match known
// bone/texture names with hashes embedded in .d3dmesh/.skl files (see BoneNames/TexNames).
public static class Crc64Ecma
{
    private const ulong Polynomial = 0x42F0E1EBA9EA3693UL;
    private static readonly ulong[] Table = BuildTable();

    public static ulong Compute(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return 0;
        }

        var crc = 0UL;
        foreach (var ch in input)
        {
            var b = (byte)(ch <= 0x7F ? ch : '_');
            if (b >= (byte)'A' && b <= (byte)'Z')
            {
                b = (byte)(b + 32);
            }

            crc = (crc << 8) ^ Table[(byte)((crc >> 56) ^ b)];
        }

        return crc;
    }

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            var crc = (ulong)i << 56;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & (1UL << 63)) != 0
                    ? (crc << 1) ^ Polynomial
                    : crc << 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
