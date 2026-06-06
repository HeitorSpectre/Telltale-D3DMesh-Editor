using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.Reinsert;

public static class GltfModelPreprocessor
{
    private static readonly HashSet<string> EyeHelperDiffuseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "map_1px_alpha",
        "color_000",
    };

    public static GltfModel ApplyGameReinsertRules(GltfModel model, GameConfig? gameConfig)
    {
        gameConfig ??= GameConfig.Current;
        if (!gameConfig.RemoveEyeHelperPrimitivesOnReimport)
        {
            return model;
        }

        return FilterPrimitives(model, primitive => !IsOpaqueEyeHelperPrimitive(model, primitive));
    }

    public static GltfModel FilterPrimitivesByDiffuseName(GltfModel model, IReadOnlySet<string> excludedDiffuseNames)
    {
        return FilterPrimitives(model, primitive =>
        {
            var diffuse = primitive.TextureSlots.TryGetValue("diffuse", out var image) ? image.Name : "";
            return string.IsNullOrWhiteSpace(diffuse) || !excludedDiffuseNames.Contains(diffuse);
        });
    }

    private static GltfModel FilterPrimitives(GltfModel model, Func<GltfPrimitive, bool> keep)
    {
        var primitives = model.Primitives.Where(keep).ToList();
        if (primitives.Count == model.Primitives.Count)
        {
            return model;
        }

        return new GltfModel
        {
            Primitives = primitives,
            Joints = model.Joints,
            Skeleton = model.Skeleton,
        };
    }

    private static bool IsOpaqueEyeHelperPrimitive(GltfModel model, GltfPrimitive primitive)
    {
        if (!primitive.TextureSlots.TryGetValue("diffuse", out var diffuse) ||
            !EyeHelperDiffuseNames.Contains(diffuse.Name))
        {
            return false;
        }

        return UsesEyeJoint(model, primitive);
    }

    private static bool UsesEyeJoint(GltfModel model, GltfPrimitive primitive)
    {
        if (primitive.Joints0 is null || model.Joints.Count == 0)
        {
            return false;
        }

        foreach (var jointIndex in primitive.Joints0)
        {
            if (jointIndex >= model.Joints.Count)
            {
                continue;
            }

            var name = model.Joints[jointIndex].Name;
            if (name is not null &&
                (name.Contains("eye", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("pupil", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
