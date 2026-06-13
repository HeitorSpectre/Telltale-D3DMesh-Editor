namespace TelltaleD3DMeshEditor.Reinsert;

// Template-patch writer: starts from the original .d3dmesh template and replaces only the requested
// regions, copying everything else byte-for-byte (headers, materials, palettes, padding, tail).
// With no patches, it returns an exact copy of the original, guaranteeing a byte-perfect roundtrip.
// Geometry edits provide RegionPatch entries for bounds, submesh table, face buffer, and vertex buffer.
public static class D3DMeshWriter
{
    public static byte[] Apply(D3DMeshLayout layout, IReadOnlyList<RegionPatch> patches)
    {
        var original = layout.Original;
        if (patches.Count == 0)
        {
            return (byte[])original.Clone();
        }

        var ordered = patches.OrderBy(p => p.Offset).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (p.Offset < 0 || p.Offset + p.OriginalLength > original.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(patches), $"Patch is outside the file: 0x{p.Offset:X}+0x{p.OriginalLength:X}");
            }
            if (i > 0 && p.Offset < ordered[i - 1].Offset + ordered[i - 1].OriginalLength)
            {
                throw new ArgumentException("Overlapping patches are not allowed.");
            }
        }

        using var ms = new MemoryStream(original.Length);
        var pos = 0;
        foreach (var p in ordered)
        {
            ms.Write(original, pos, p.Offset - pos);
            ms.Write(p.NewBytes, 0, p.NewBytes.Length);
            pos = p.Offset + p.OriginalLength;
        }

        ms.Write(original, pos, original.Length - pos);
        return ms.ToArray();
    }
}

// Replaces [Offset, Offset+OriginalLength) in the template with NewBytes, which may have a different size.
public sealed record RegionPatch(int Offset, int OriginalLength, byte[] NewBytes);
