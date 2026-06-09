namespace TelltaleD3DMeshEditor.Formats.Mesh;

// The per-vertex bone indices stored in <see cref="VertexData"/> follow two different conventions
// depending on the mesh version, and every consumer must apply the same one or a vertex resolves to the
// wrong bone-palette slot (e.g. a right-leg vertex driven by a left-leg bone, and pose edits leaking
// across the body):
//   * v17 / v18: the parser already converts the raw byte to a DIRECT bone-palette index, so the value
//     is used as-is.
//   * older versions (e.g. v13/v14): the parser keeps the RAW byte, which is the palette index times 3,
//     so consumers divide by 3 to get the palette index.
// Both the preview skinning and the glTF export route through this single helper so they stay in
// agreement with the parser and with each other.
public static class BoneIndexConvention
{
    public static int ToPaletteIndex(int rawBone, int meshVersion)
        => meshVersion is 17 or 18 ? rawBone : rawBone / 3;
}
