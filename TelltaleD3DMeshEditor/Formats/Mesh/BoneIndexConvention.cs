namespace TelltaleD3DMeshEditor.Formats.Mesh;

// The per-vertex bone indices stored in <see cref="VertexData"/> follow two different conventions
// depending on the mesh version, and every consumer must apply the same one or a vertex resolves to the
// wrong bone-palette slot (e.g. a right-leg vertex driven by a left-leg bone, and pose edits leaking
// across the body):
//   * v1 / v17 / v18 / v25: the parser already converts the raw byte to a DIRECT bone index/palette
//     index, so the value is used as-is.
//   * older 5VSM/4VSM versions (e.g. v12/v13/v14): the parser keeps the RAW byte, which is the palette
//     index times 3, so consumers divide by 3 to get the palette index.
//   * the Tales from the Borderlands source-leak ERTM versions (v5/v6/v9/v10/v11) store the palette index
//     times 4, so consumers divide by 4. (Only ERTM exists at these versions; ERTM v12 overlaps with
//     skinned 5VSM v12, but ERTM v12 meshes are unskinned UI so they keep the /3 path harmlessly.)
// Both the preview skinning and the glTF export route through this single helper so they stay in
// agreement with the parser and with each other.
public static class BoneIndexConvention
{
    public static int ToPaletteIndex(int rawBone, int meshVersion)
        => meshVersion is 1 or 17 or 18 or 25 ? rawBone
            : meshVersion is 5 or 6 or 9 or 10 or 11 ? rawBone / 4
            : rawBone / 3;
}
