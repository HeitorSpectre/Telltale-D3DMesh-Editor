using System.Numerics;

namespace TelltaleD3DMeshEditor.Reinsert;

// Optional "match original model size" step for reimport. Some exported GLBs carry an unintended scale
// (e.g. a Blender node scale of 10 that was never applied), so the imported model enters the game far
// larger or smaller than the asset it replaces. When enabled, this uniformly rescales and recenters the
// imported geometry so its HEIGHT (the vertical Y axis) matches the original mesh's height. The scale is
// uniform, so width/depth follow proportionally and the model is never stretched; it is centered where
// the original sat. Height drives the fit because that is what reads as "how big" an object is in the
// scene (a model that is proportionally tall should not be shrunk just because it is also narrow).
public static class GltfModelScaler
{
    public static void MatchBounds(GltfModel model, (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) target)
    {
        if (!TryComputeBounds(model, out var importMin, out var importMax))
        {
            return;
        }

        var targetMin = new Vector3(target.MinX, target.MinY, target.MinZ);
        var targetMax = new Vector3(target.MaxX, target.MaxY, target.MaxZ);

        // Drive the uniform scale by the height (Y) extent only. glTF (and these Telltale meshes) are
        // Y-up, so Y is top-to-bottom. Falling back to the largest extent keeps flat/ground-plane meshes
        // (zero height) from collapsing to scale 0.
        var importHeight = importMax.Y - importMin.Y;
        var targetHeight = targetMax.Y - targetMin.Y;
        if (importHeight < 1e-9f || targetHeight < 1e-9f)
        {
            importHeight = (importMax - importMin).Length();
            targetHeight = (targetMax - targetMin).Length();
        }

        if (importHeight < 1e-9f || targetHeight < 1e-9f)
        {
            return;
        }

        var scale = targetHeight / importHeight;
        var importCenter = (importMin + importMax) * 0.5f;
        var targetCenter = (targetMin + targetMax) * 0.5f;

        foreach (var primitive in model.Primitives)
        {
            var positions = primitive.Positions;
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = targetCenter + (positions[i] - importCenter) * scale;
            }
        }
    }

    private static bool TryComputeBounds(GltfModel model, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        var any = false;
        foreach (var primitive in model.Primitives)
        {
            foreach (var position in primitive.Positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                any = true;
            }
        }

        return any;
    }
}
