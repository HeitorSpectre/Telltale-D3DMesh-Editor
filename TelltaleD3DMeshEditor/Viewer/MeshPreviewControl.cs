using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.Viewer;

// Software 3D viewer (GDI+): rasterizes the mesh with a z-buffer, applies diffuse/detail/bake/
// shadow/normal layers, draws the skeleton, and supports rotate, pan, zoom, and pose editing.
// It does not require GPU/OpenGL and runs on any Windows setup. Preview only.
public sealed class MeshPreviewControl : Control
{
    // Default camera. Switching models resets to these values so orientation/zoom/pan
    // do not leak from the previous model.
    private const float DefaultYaw = -0.55f;
    private const float DefaultPitch = 0.25f;
    private const float DefaultZoom = 1.0f;
    private const float DepthTieEpsilon = 0.00005f;

    private MeshData? _mesh;
    private SkeletonData? _skeleton;
    private IReadOnlyDictionary<int, MaterialTextureSet> _textures = new Dictionary<int, MaterialTextureSet>();
    private MeshBounds _bounds;
    private bool _hasBounds;
    private string _sizeInfo = "";
    private int[] _pixelBuffer = [];
    private float[] _depthBuffer = [];
    private Bitmap? _meshBitmap;
    private Point _lastMouse;
    private float _yaw = DefaultYaw;
    private float _pitch = DefaultPitch;
    private float _zoom = DefaultZoom;
    private Vector2 _pan;
    private bool _showSkeleton;
    private bool _showFaces = true;
    private bool _showPolygons;
    private bool _panMode;
    private bool _poseMode;
    private int _selectedBone = -1;
    private readonly Dictionary<int, Vector3> _boneOffsets = new();
    private readonly Dictionary<int, Quaternion> _boneRotations = new();

    public MeshPreviewControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(122, 122, 120);
        ForeColor = Color.Gainsboro;
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void SetScene(MeshData? mesh, SkeletonData? skeleton, IReadOnlyDictionary<int, MaterialTextureSet>? textures = null)
    {
        _mesh = mesh;
        _skeleton = skeleton;
        _textures = textures ?? new Dictionary<int, MaterialTextureSet>();
        _bounds = mesh is null ? default : ComputeBounds(mesh);
        _hasBounds = mesh is not null;
        _sizeInfo = mesh is null ? "" : BuildSizeInfo(mesh);
        _boneOffsets.Clear();
        _boneRotations.Clear();
        _selectedBone = -1;
        // Reset the camera to the default view so the new model does not inherit the previous orientation.
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        Fit();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _meshBitmap?.Dispose();
        }

        base.Dispose(disposing);
    }

    public void Fit()
    {
        _zoom = DefaultZoom;
        _pan = Vector2.Zero;
        Invalidate();
    }

    public void SetCamera(float yaw, float pitch, float zoom = 1.0f, Vector2? pan = null)
    {
        _yaw = yaw;
        _pitch = Math.Clamp(pitch, -1.45f, 1.45f);
        _zoom = Math.Clamp(zoom, 0.05f, 50f);
        _pan = pan ?? Vector2.Zero;
        Invalidate();
    }

    public void SetPanMode(bool enabled)
    {
        _panMode = enabled;
        Cursor = enabled ? Cursors.SizeAll : Cursors.Default;
        Invalidate();
    }

    public void SetPoseMode(bool enabled)
    {
        _poseMode = enabled;
        if (enabled)
        {
            _showSkeleton = true;
        }
        else
        {
            _showSkeleton = false;
            _selectedBone = -1;
        }

        Cursor = enabled ? Cursors.Cross : _panMode ? Cursors.SizeAll : Cursors.Default;
        Invalidate();
    }

    public void ResetPose()
    {
        _boneOffsets.Clear();
        _boneRotations.Clear();
        _selectedBone = -1;
        Invalidate();
    }

    public void RotateBoneByHash(ulong hash, Quaternion delta)
    {
        if (_skeleton is null)
        {
            return;
        }

        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            if (_skeleton.Bones[i].Hash != hash)
            {
                continue;
            }

            _boneRotations.TryGetValue(i, out var existing);
            if (existing.LengthSquared() < 0.000001f)
            {
                existing = Quaternion.Identity;
            }

            _boneRotations[i] = Quaternion.Normalize(delta * existing);
        }

        Invalidate();
    }

    public void ToggleSkeleton()
    {
        _showSkeleton = !_showSkeleton;
        Invalidate();
    }

    public void SetSkeletonVisible(bool visible)
    {
        _showSkeleton = visible;
        if (!visible)
        {
            _selectedBone = -1;
        }

        Invalidate();
    }

    public void ToggleFaces()
    {
        _showFaces = !_showFaces;
        Invalidate();
    }

    public void TogglePolygons()
    {
        _showPolygons = !_showPolygons;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _lastMouse = e.Location;
        if (_poseMode && e.Button == MouseButtons.Left)
        {
            _selectedBone = PickBone(e.Location);
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var dx = e.X - _lastMouse.X;
        var dy = e.Y - _lastMouse.Y;
        if (_poseMode && e.Button == MouseButtons.Left && _selectedBone >= 0)
        {
            MoveSelectedBone(dx, dy);
            _lastMouse = e.Location;
            Invalidate();
        }
        else if (IsPanGesture(e))
        {
            _pan += new Vector2(dx, dy);
            _lastMouse = e.Location;
            Invalidate();
        }
        else if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
        {
            _yaw += dx * 0.01f;
            _pitch += dy * 0.01f;
            _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
            _lastMouse = e.Location;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ZoomAt(e.Location, e.Delta > 0 ? 1.14f : 0.88f);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
        {
            Fit();
        }

        base.OnMouseDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F || e.KeyCode == Keys.Home)
        {
            Fit();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private bool IsPanGesture(MouseEventArgs e)
    {
        return e.Button == MouseButtons.Middle
            || (_panMode && e.Button == MouseButtons.Left)
            || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) == Keys.Shift);
    }

    private void ZoomAt(Point mouseLocation, float factor)
    {
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.08f, 30f);
        var ratio = _zoom / oldZoom;
        var baseCenter = new Vector2(ClientSize.Width * 0.5f, ClientSize.Height * 0.53f);
        var oldCenter = baseCenter + _pan;
        var mouse = new Vector2(mouseLocation.X, mouseLocation.Y);
        var newCenter = mouse - (mouse - oldCenter) * ratio;
        _pan = newCenter - baseCenter;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawStudioBackground(g);

        if (_mesh is null)
        {
            DrawCentered(g, "Select a .d3dmesh file to preview");
            return;
        }

        var bounds = _hasBounds ? _bounds : ComputeBounds(_mesh);
        if (bounds.Radius <= 0)
        {
            DrawCentered(g, "This model has no vertices to display");
            return;
        }

        var transform = Matrix4x4.CreateTranslation(-bounds.Center) *
                        Matrix4x4.CreateRotationX(_pitch) *
                        Matrix4x4.CreateRotationY(_yaw);
        var scale = MathF.Min(ClientSize.Width, ClientSize.Height) * 0.42f / bounds.Radius * _zoom;
        var center = GetViewportCenter();

        DrawGroundShadow(g, bounds.Radius, scale, center);
        DrawMesh(g, transform, scale, center);
        if (_showSkeleton && _skeleton is not null && _skeleton.Bones.Count > 0)
        {
            DrawSkeleton(g, transform, scale, center);
        }

        using var textBrush = new SolidBrush(Color.FromArgb(230, 245, 245, 245));
        var info = $"{_mesh.Name}  |  submeshes: {_mesh.Submeshes.Count}  vertices: {_mesh.VertexCount}  polygons: {_mesh.FaceCount}";
        if (_skeleton is not null)
        {
            info += $"  bones: {_skeleton.Bones.Count}";
        }

        g.DrawString(info, Font, textBrush, 10, 10);
        if (_sizeInfo.Length > 0)
        {
            g.DrawString(_sizeInfo, Font, textBrush, 10, 28);
        }
    }

    private (int[] Pixels, float[] Depth, Bitmap Bitmap) PrepareRasterBuffers(int width, int height)
    {
        var pixelCount = checked(width * height);
        if (_pixelBuffer.Length != pixelCount)
        {
            _pixelBuffer = new int[pixelCount];
        }
        else
        {
            Array.Clear(_pixelBuffer, 0, pixelCount);
        }

        if (_depthBuffer.Length != pixelCount)
        {
            _depthBuffer = new float[pixelCount];
        }

        Array.Fill(_depthBuffer, float.NegativeInfinity, 0, pixelCount);
        if (_meshBitmap is null || _meshBitmap.Width != width || _meshBitmap.Height != height)
        {
            _meshBitmap?.Dispose();
            _meshBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        return (_pixelBuffer, _depthBuffer, _meshBitmap);
    }

    private void DrawMesh(Graphics g, Matrix4x4 transform, float scale, PointF center)
    {
        if (_showFaces)
        {
            DrawSolidMesh(g, transform, scale, center);
        }

        if (_showPolygons || !_showFaces)
        {
            DrawPolygons(g, transform, scale, center);
        }
    }

    private void DrawSolidMesh(Graphics g, Matrix4x4 transform, float scale, PointF center)
    {
        var viewportWidth = ClientSize.Width;
        var viewportHeight = ClientSize.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        var width = viewportWidth;
        var height = viewportHeight;
        var (pixels, depth, bitmap) = PrepareRasterBuffers(width, height);
        var light = Vector3.Normalize(new Vector3(-0.45f, -0.65f, 1f));
        var baseBoneMatrices = _skeleton is not null ? BuildBoneWorldMatrices(_skeleton, null, null) : null;
        var hasPose = _boneOffsets.Count > 0 || _boneRotations.Count > 0;
        var posedBoneMatrices = _skeleton is not null && hasPose ? BuildBoneWorldMatrices(_skeleton, _boneOffsets, _boneRotations) : baseBoneMatrices;

        for (var submeshIndex = 0; submeshIndex < _mesh!.Submeshes.Count; submeshIndex++)
        {
            var submesh = _mesh.Submeshes[submeshIndex];
            if (IsNullPreviewMaterial(submesh))
            {
                continue;
            }

            _textures.TryGetValue(submeshIndex, out var textures);
            var boneMap = BuildBoneMap(submesh);
            var renderVertices = new RenderVertex[submesh.Vertices.Count];
            for (var i = 0; i < submesh.Vertices.Count; i++)
            {
                var vertex = submesh.Vertices[i];
                var skinned = ApplySkinning(vertex, boneMap, baseBoneMatrices, posedBoneMatrices);
                var view = Vector3.Transform(skinned, transform);
                var screen = Project(view, scale, center);
                var normal = Vector3.TransformNormal(ToNormal(vertex), transform);
                if (normal.LengthSquared() < 0.000001f)
                {
                    normal = Vector3.UnitZ;
                }
                else
                {
                    normal = Vector3.Normalize(normal);
                }

                var shade = Math.Clamp(0.58f + MathF.Abs(Vector3.Dot(normal, light)) * 0.42f, 0.50f, 1.0f);
                var (detailU, detailV) = SelectDetailUv(vertex);
                var (bakeU, bakeV) = SelectBakeUv(vertex);
                var (shadowU, shadowV) = SelectShadowUv(vertex);
                renderVertices[i] = new RenderVertex(
                    screen.X, screen.Y, view.Z, shade,
                    vertex.U, vertex.V, detailU, detailV, bakeU, bakeV, shadowU, shadowV,
                    vertex.ColorR, vertex.ColorG, vertex.ColorB, vertex.ColorA);
            }

            foreach (var (a, b, c) in submesh.Faces)
            {
                if ((uint)a >= renderVertices.Length || (uint)b >= renderVertices.Length || (uint)c >= renderVertices.Length)
                {
                    continue;
                }

                RasterizeTriangle(renderVertices[a], renderVertices[b], renderVertices[c], textures, pixels, depth, width, height);
            }
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        g.DrawImageUnscaled(bitmap, 0, 0);
    }

    private void DrawPolygons(Graphics g, Matrix4x4 transform, float scale, PointF center)
    {
        using var edgePen = new Pen(Color.FromArgb(_showFaces ? 72 : 190, 64, 72, 82), _showFaces ? 0.8f : 1f);
        var baseBoneMatrices = _skeleton is not null ? BuildBoneWorldMatrices(_skeleton, null, null) : null;
        var hasPose = _boneOffsets.Count > 0 || _boneRotations.Count > 0;
        var posedBoneMatrices = _skeleton is not null && hasPose ? BuildBoneWorldMatrices(_skeleton, _boneOffsets, _boneRotations) : baseBoneMatrices;
        foreach (var submesh in _mesh!.Submeshes)
        {
            if (IsNullPreviewMaterial(submesh))
            {
                continue;
            }

            var boneMap = BuildBoneMap(submesh);
            var points = new PointF[submesh.Vertices.Count];
            for (var i = 0; i < submesh.Vertices.Count; i++)
            {
                points[i] = Project(ApplySkinning(submesh.Vertices[i], boneMap, baseBoneMatrices, posedBoneMatrices), transform, scale, center);
            }

            foreach (var (a, b, c) in submesh.Faces)
            {
                if ((uint)a >= points.Length || (uint)b >= points.Length || (uint)c >= points.Length)
                {
                    continue;
                }

                g.DrawPolygon(edgePen, [points[a], points[b], points[c]]);
            }
        }
    }

    private static void RasterizeTriangle(RenderVertex a, RenderVertex b, RenderVertex c, MaterialTextureSet? textures, int[] pixels, float[] depth, int width, int height)
    {
        var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (MathF.Abs(area) < 0.00001f)
        {
            return;
        }

        var rawMinX = MathF.Min(a.X, MathF.Min(b.X, c.X));
        var rawMaxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        var rawMinY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
        var rawMaxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (rawMaxX < 0f || rawMinX > width - 1 || rawMaxY < 0f || rawMinY > height - 1)
        {
            return;
        }

        var minX = Math.Clamp((int)MathF.Floor(rawMinX), 0, width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling(rawMaxX), 0, width - 1);
        var minY = Math.Clamp((int)MathF.Floor(rawMinY), 0, height - 1);
        var maxY = Math.Clamp((int)MathF.Ceiling(rawMaxY), 0, height - 1);
        var baseColor = Color.FromArgb(255, 226, 229, 229);
        var invArea = 1f / area;
        var w0StepX = (c.Y - b.Y) * invArea;
        var w1StepX = (a.Y - c.Y) * invArea;

        for (var y = minY; y <= maxY; y++)
        {
            var sampleX = minX + 0.5f;
            var sampleY = y + 0.5f;
            var rowW0 = Edge(b.X, b.Y, c.X, c.Y, sampleX, sampleY) * invArea;
            var rowW1 = Edge(c.X, c.Y, a.X, a.Y, sampleX, sampleY) * invArea;
            for (var x = minX; x <= maxX; x++)
            {
                var w0 = rowW0;
                var w1 = rowW1;
                rowW0 += w0StepX;
                rowW1 += w1StepX;
                var w2 = 1f - w0 - w1;
                if (w0 < -0.0001f || w1 < -0.0001f || w2 < -0.0001f)
                {
                    continue;
                }

                var z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                var index = y * width + x;
                if (z <= depth[index] + DepthTieEpsilon)
                {
                    continue;
                }

                var shade = Math.Clamp(a.Shade * w0 + b.Shade * w1 + c.Shade * w2, 0.45f, 1.08f);
                var vertexColorR = Math.Clamp(a.ColorR * w0 + b.ColorR * w1 + c.ColorR * w2, 0f, 1f);
                var vertexColorG = Math.Clamp(a.ColorG * w0 + b.ColorG * w1 + c.ColorG * w2, 0f, 1f);
                var vertexColorB = Math.Clamp(a.ColorB * w0 + b.ColorB * w1 + c.ColorB * w2, 0f, 1f);
                var vertexColorA = Math.Clamp(a.ColorA * w0 + b.ColorA * w1 + c.ColorA * w2, 0f, 1f);
                int color;
                if (textures?.Diffuse is null)
                {
                    color = ShadeColor(baseColor, shade).ToArgb();
                }
                else
                {
                    var u = a.U * w0 + b.U * w1 + c.U * w2;
                    var v = a.V * w0 + b.V * w1 + c.V * w2;
                    var normalBoost = SampleNormalBoost(textures.Normal, u, v);
                    color = ShadeTexture(textures.Diffuse.Sample(u, v), shade * normalBoost);
                    var detailU = a.DetailU * w0 + b.DetailU * w1 + c.DetailU * w2;
                    var detailV = a.DetailV * w0 + b.DetailV * w1 + c.DetailV * w2;
                    color = ApplyDetail(color, textures.Detail, detailU, detailV);
                    var bakeU = a.BakeU * w0 + b.BakeU * w1 + c.BakeU * w2;
                    var bakeV = a.BakeV * w0 + b.BakeV * w1 + c.BakeV * w2;
                    color = ApplyBake(color, textures.Bake, bakeU, bakeV);
                    var shadowU = a.ShadowU * w0 + b.ShadowU * w1 + c.ShadowU * w2;
                    var shadowV = a.ShadowV * w0 + b.ShadowV * w1 + c.ShadowV * w2;
                    color = ApplyShadow(color, textures.Shadow, shadowU, shadowV);
                }

                color = ApplyVertexColor(color, vertexColorR, vertexColorG, vertexColorB, vertexColorA);
                var pixelAlpha = (color >> 24) & 0xFF;
                if (pixelAlpha < 8)
                {
                    continue;
                }

                // Semi-transparent pixels (e.g. Sarah's glasses lens, soft hair edges) blend over what
                // is already drawn behind them instead of overwriting it opaquely. Fully opaque pixels
                // (the vast majority, including all of The Wolf Among Us) are written unchanged.
                if (pixelAlpha < 250)
                {
                    color = BlendOver(color, pixels[index], pixelAlpha);
                }

                depth[index] = z;
                pixels[index] = color;
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy)
    {
        return (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);
    }

    // Source-over alpha blend of a semi-transparent pixel onto the pixel already in the framebuffer.
    private static int BlendOver(int src, int dst, int srcAlpha)
    {
        var inv = 255 - srcAlpha;
        var r = (((src >> 16) & 0xFF) * srcAlpha + ((dst >> 16) & 0xFF) * inv) / 255;
        var g = (((src >> 8) & 0xFF) * srcAlpha + ((dst >> 8) & 0xFF) * inv) / 255;
        var b = ((src & 0xFF) * srcAlpha + (dst & 0xFF) * inv) / 255;
        return (0xFF << 24) | (r << 16) | (g << 8) | b;
    }

    private void DrawSkeleton(Graphics g, Matrix4x4 transform, float scale, PointF center)
    {
        var world = BuildBoneWorldPositions(_skeleton!, _boneOffsets, _boneRotations);
        using var bonePen = new Pen(Color.FromArgb(240, 255, 196, 72), 2f);
        using var jointBrush = new SolidBrush(Color.FromArgb(245, 255, 235, 145));
        using var selectedPen = new Pen(Color.FromArgb(255, 80, 225, 255), 3f);
        using var selectedBrush = new SolidBrush(Color.FromArgb(255, 80, 225, 255));

        for (var i = 0; i < _skeleton!.Bones.Count; i++)
        {
            var parent = _skeleton.Bones[i].ParentIndex;
            var p = Project(world[i], transform, scale, center);
            var isSelected = i == _selectedBone;
            g.FillEllipse(isSelected ? selectedBrush : jointBrush, p.X - 2.5f, p.Y - 2.5f, 5f, 5f);
            if (parent >= 0 && parent < world.Length)
            {
                var pp = Project(world[parent], transform, scale, center);
                g.DrawLine(isSelected ? selectedPen : bonePen, pp, p);
            }
        }
    }

    private static Vector3[] BuildBoneWorldPositions(
        SkeletonData skeleton,
        IReadOnlyDictionary<int, Vector3>? offsets = null,
        IReadOnlyDictionary<int, Quaternion>? rotations = null)
    {
        var worldMatrices = BuildBoneWorldMatrices(skeleton, offsets, rotations);
        var result = new Vector3[skeleton.Bones.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Vector3.Transform(Vector3.Zero, worldMatrices[i]);
        }

        return result;
    }

    private static Matrix4x4[] BuildBoneWorldMatrices(
        SkeletonData skeleton,
        IReadOnlyDictionary<int, Vector3>? offsets,
        IReadOnlyDictionary<int, Quaternion>? rotations)
    {
        var worldMatrices = new Matrix4x4[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            var bone = skeleton.Bones[i];
            var rotation = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw);
            if (rotation.LengthSquared() < 0.000001f)
            {
                rotation = Quaternion.Identity;
            }
            else
            {
                rotation = Quaternion.Normalize(rotation);
            }

            var offset = Vector3.Zero;
            offsets?.TryGetValue(i, out offset);
            if (rotations is not null && rotations.TryGetValue(i, out var extraRotation))
            {
                rotation = Quaternion.Normalize(extraRotation * rotation);
            }

            var local =
                Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation(bone.X + offset.X, bone.Y + offset.Y, bone.Z + offset.Z);

            var parent = bone.ParentIndex;
            worldMatrices[i] = parent >= 0 && parent < i
                ? local * worldMatrices[parent]
                : local;
        }

        return worldMatrices;
    }

    private int[]? BuildBoneMap(SubmeshData submesh)
    {
        if (_mesh is null || _skeleton is null || _mesh.BonePalettes.Count == 0)
        {
            return null;
        }

        var paletteIndex = Math.Clamp(submesh.BonePaletteIndex, 0, _mesh.BonePalettes.Count - 1);
        var palette = _mesh.BonePalettes[paletteIndex];
        var skeletonByHash = new Dictionary<ulong, int>(palette.Length);
        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            skeletonByHash[_skeleton.Bones[i].Hash] = i;
        }

        var map = new int[palette.Length];
        for (var i = 0; i < palette.Length; i++)
        {
            map[i] = skeletonByHash.TryGetValue(palette[i], out var skeletonIndex) ? skeletonIndex : -1;
        }

        return map;
    }

    private static Vector3 ApplySkinning(VertexData vertex, int[]? boneMap, Matrix4x4[]? baseMatrices, Matrix4x4[]? posedMatrices)
    {
        var original = ToVector(vertex);
        if (boneMap is null || baseMatrices is null || posedMatrices is null || ReferenceEquals(baseMatrices, posedMatrices))
        {
            return original;
        }

        var result = Vector3.Zero;
        var total = 0f;
        AccumulateSkinned(vertex.Bone0, vertex.Weight0, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone1, vertex.Weight1, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone2, vertex.Weight2, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone3, vertex.Weight3, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);

        if (total <= 0.000001f)
        {
            return original;
        }

        return result / total;
    }

    private static void AccumulateSkinned(
        int paletteBone,
        float weight,
        Vector3 original,
        int[] boneMap,
        Matrix4x4[] baseMatrices,
        Matrix4x4[] posedMatrices,
        ref Vector3 result,
        ref float total)
    {
        if (weight <= 0.000001f || paletteBone < 0)
        {
            return;
        }

        var paletteIndex = NormalizePaletteBoneIndex(paletteBone, boneMap.Length);
        if (paletteIndex < 0)
        {
            return;
        }

        var skeletonBone = boneMap[paletteIndex];
        if (skeletonBone < 0 || skeletonBone >= baseMatrices.Length || skeletonBone >= posedMatrices.Length)
        {
            return;
        }

        if (!Matrix4x4.Invert(baseMatrices[skeletonBone], out var inverseBase))
        {
            return;
        }

        var skinMatrix = inverseBase * posedMatrices[skeletonBone];
        result += Vector3.Transform(original, skinMatrix) * weight;
        total += weight;
    }

    private static int NormalizePaletteBoneIndex(int rawIndex, int paletteLength)
    {
        if (rawIndex >= 0 && rawIndex % 3 == 0)
        {
            var divided = rawIndex / 3;
            if (divided < paletteLength)
            {
                return divided;
            }
        }

        if (rawIndex >= 0 && rawIndex < paletteLength)
        {
            return rawIndex;
        }

        return -1;
    }

    private int PickBone(Point location)
    {
        if (_mesh is null || _skeleton is null || _skeleton.Bones.Count == 0)
        {
            return -1;
        }

        var bounds = _hasBounds ? _bounds : ComputeBounds(_mesh);
        if (bounds.Radius <= 0)
        {
            return -1;
        }

        var transform = BuildViewTransform(bounds);
        var scale = GetViewScale(bounds);
        var center = GetViewportCenter();
        var world = BuildBoneWorldPositions(_skeleton, _boneOffsets, _boneRotations);
        var bestIndex = -1;
        var bestDistance = 14f;
        for (var i = 0; i < world.Length; i++)
        {
            var p = Project(world[i], transform, scale, center);
            var distance = MathF.Sqrt((p.X - location.X) * (p.X - location.X) + (p.Y - location.Y) * (p.Y - location.Y));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            var parent = _skeleton.Bones[i].ParentIndex;
            if (parent < 0 || parent >= world.Length)
            {
                continue;
            }

            var a = Project(world[parent], transform, scale, center);
            var b = Project(world[i], transform, scale, center);
            var distance = DistanceToSegment(location, a, b);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return FindPoseBoneWithInfluence(bestIndex);
    }

    private static float DistanceToSegment(Point p, PointF a, PointF b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var apx = p.X - a.X;
        var apy = p.Y - a.Y;
        var len = abx * abx + aby * aby;
        if (len <= 0.0001f)
        {
            return MathF.Sqrt(apx * apx + apy * apy);
        }

        var t = Math.Clamp((apx * abx + apy * aby) / len, 0f, 1f);
        var x = a.X + abx * t;
        var y = a.Y + aby * t;
        var dx = p.X - x;
        var dy = p.Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void MoveSelectedBone(float dx, float dy)
    {
        if (_mesh is null || _skeleton is null || _selectedBone < 0)
        {
            return;
        }

        var bounds = _hasBounds ? _bounds : ComputeBounds(_mesh);
        if (bounds.Radius <= 0)
        {
            return;
        }

        var scale = GetViewScale(bounds);
        if (scale <= 0)
        {
            return;
        }

        var viewRotation = Matrix4x4.CreateRotationX(_pitch) * Matrix4x4.CreateRotationY(_yaw);
        if (!Matrix4x4.Invert(viewRotation, out var inverseRotation))
        {
            return;
        }

        if (_skeleton.Bones[_selectedBone].ParentIndex >= 0)
        {
            RotateBoneFromDrag(_selectedBone, dx, dy, inverseRotation);
        }
        else
        {
            var viewDelta = new Vector3(dx / scale, -dy / scale, 0f);
            var modelDelta = Vector3.TransformNormal(viewDelta, inverseRotation);
            AddBoneOffsetFromWorldDelta(_selectedBone, modelDelta);
        }
    }

    private void RotateBoneFromDrag(int boneIndex, float dx, float dy, Matrix4x4 inverseRotation)
    {
        if (_skeleton is null || boneIndex < 0 || boneIndex >= _skeleton.Bones.Count)
        {
            return;
        }

        var viewRight = Vector3.TransformNormal(Vector3.UnitX, inverseRotation);
        var viewUp = Vector3.TransformNormal(Vector3.UnitY, inverseRotation);
        if (viewRight.LengthSquared() < 0.000001f || viewUp.LengthSquared() < 0.000001f)
        {
            return;
        }

        var yaw = Quaternion.CreateFromAxisAngle(Vector3.Normalize(viewUp), dx * 0.012f);
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.Normalize(viewRight), dy * 0.012f);
        var delta = Quaternion.Normalize(pitch * yaw);
        _boneRotations.TryGetValue(boneIndex, out var existing);
        if (existing.LengthSquared() < 0.000001f)
        {
            existing = Quaternion.Identity;
        }

        _boneRotations[boneIndex] = Quaternion.Normalize(delta * existing);
    }

    private void AddBoneOffsetFromWorldDelta(int boneIndex, Vector3 worldDelta)
    {
        if (_skeleton is null || boneIndex < 0 || boneIndex >= _skeleton.Bones.Count)
        {
            return;
        }

        var parent = _skeleton.Bones[boneIndex].ParentIndex;
        var localDelta = worldDelta;
        if (parent >= 0 && parent < boneIndex)
        {
            var parentWorld = BuildBoneWorldMatrices(_skeleton, _boneOffsets, _boneRotations)[parent];
            parentWorld.M41 = 0f;
            parentWorld.M42 = 0f;
            parentWorld.M43 = 0f;
            if (Matrix4x4.Invert(parentWorld, out var inverseParent))
            {
                localDelta = Vector3.TransformNormal(worldDelta, inverseParent);
            }
        }

        _boneOffsets.TryGetValue(boneIndex, out var existing);
        _boneOffsets[boneIndex] = existing + localDelta;
    }

    private int FindPoseBoneWithInfluence(int boneIndex)
    {
        if (_mesh is null || _skeleton is null || boneIndex < 0)
        {
            return boneIndex;
        }

        var influenced = BuildInfluencedSkeletonBones();
        var current = boneIndex;
        while (current >= 0 && !influenced.Contains(current))
        {
            current = _skeleton.Bones[current].ParentIndex;
        }

        return current >= 0 ? current : boneIndex;
    }

    private HashSet<int> BuildInfluencedSkeletonBones()
    {
        var influenced = new HashSet<int>();
        if (_mesh is null || _skeleton is null)
        {
            return influenced;
        }

        foreach (var submesh in _mesh.Submeshes)
        {
            var map = BuildBoneMap(submesh);
            if (map is null)
            {
                continue;
            }

            foreach (var vertex in submesh.Vertices)
            {
                AddInfluencedBone(vertex.Bone0, vertex.Weight0, map, influenced);
                AddInfluencedBone(vertex.Bone1, vertex.Weight1, map, influenced);
                AddInfluencedBone(vertex.Bone2, vertex.Weight2, map, influenced);
                AddInfluencedBone(vertex.Bone3, vertex.Weight3, map, influenced);
            }
        }

        return influenced;
    }

    private static void AddInfluencedBone(int rawBone, float weight, int[] map, HashSet<int> influenced)
    {
        if (weight <= 0.000001f)
        {
            return;
        }

        var index = NormalizePaletteBoneIndex(rawBone, map.Length);
        if (index >= 0 && index < map.Length && map[index] >= 0)
        {
            influenced.Add(map[index]);
        }
    }

    private Matrix4x4 BuildViewTransform(MeshBounds bounds)
    {
        return Matrix4x4.CreateTranslation(-bounds.Center) *
               Matrix4x4.CreateRotationX(_pitch) *
               Matrix4x4.CreateRotationY(_yaw);
    }

    private float GetViewScale(MeshBounds bounds)
    {
        return MathF.Min(ClientSize.Width, ClientSize.Height) * 0.42f / bounds.Radius * _zoom;
    }

    private PointF GetViewportCenter()
    {
        return new PointF(ClientSize.Width * 0.5f + _pan.X, ClientSize.Height * 0.53f + _pan.Y);
    }

    private static MeshBounds ComputeBounds(MeshData mesh)
    {
        var any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in mesh.Submeshes.SelectMany(s => s.Vertices))
        {
            var p = ToVector(vertex);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }

        if (!any)
        {
            return new MeshBounds(Vector3.Zero, 0);
        }

        var center = (min + max) * 0.5f;
        var radius = MathF.Max(Vector3.Distance(min, center), Vector3.Distance(max, center));
        return new MeshBounds(center, radius);
    }

    private static string BuildSizeInfo(MeshData mesh)
    {
        var any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var submesh in mesh.Submeshes)
        {
            foreach (var vertex in submesh.Vertices)
            {
                var p = ToVector(vertex);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        if (!any)
        {
            return "";
        }

        var size = Vector3.Max(max - min, Vector3.Zero);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"size: X {FormatMeters(size.X)} m | Y/height {FormatMeters(size.Y)} m | Z {FormatMeters(size.Z)} m ({FormatCentimeters(size.X)} x {FormatCentimeters(size.Y)} x {FormatCentimeters(size.Z)} cm)");
    }

    private static string FormatMeters(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatCentimeters(float value)
    {
        return (value * 100f).ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void DrawStudioBackground(Graphics g)
    {
        using var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(142, 142, 140),
            Color.FromArgb(102, 102, 100),
            LinearGradientMode.Vertical);
        g.FillRectangle(brush, ClientRectangle);
    }

    private static void DrawGroundShadow(Graphics g, float radius, float scale, PointF center)
    {
        var width = Math.Clamp(radius * scale * 1.25f, 80f, 420f);
        var height = Math.Clamp(radius * scale * 0.18f, 18f, 90f);
        var rect = new RectangleF(center.X - width * 0.5f, center.Y + radius * scale * 0.72f, width, height);
        using var path = new GraphicsPath();
        path.AddEllipse(rect);
        using var shadow = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(58, 52, 52, 52),
            SurroundColors = [Color.FromArgb(0, 52, 52, 52)]
        };
        g.FillPath(shadow, path);
    }

    private void DrawCentered(Graphics g, string text)
    {
        using var brush = new SolidBrush(Color.FromArgb(235, 245, 245, 245));
        var size = g.MeasureString(text, Font);
        g.DrawString(text, Font, brush, (Width - size.Width) * 0.5f, (Height - size.Height) * 0.5f);
    }

    private static Vector3 ToVector(VertexData v) => new(v.X, v.Y, v.Z);
    private static Vector3 ToNormal(VertexData v) => new(v.Nx, v.Ny, v.Nz);

    private static (float U, float V) SelectDetailUv(VertexData vertex)
    {
        if (HasDifferentUv(vertex.U, vertex.V, vertex.U3, vertex.V3))
        {
            return (vertex.U3, vertex.V3);
        }

        return (vertex.U, vertex.V);
    }

    private static (float U, float V) SelectBakeUv(VertexData vertex)
    {
        if (HasDifferentUv(vertex.U, vertex.V, vertex.U2, vertex.V2))
        {
            return (vertex.U2, vertex.V2);
        }

        return (vertex.U, vertex.V);
    }

    private static (float U, float V) SelectShadowUv(VertexData vertex)
    {
        if (HasDifferentUv(vertex.U, vertex.V, vertex.U4, vertex.V4))
        {
            return (vertex.U4, vertex.V4);
        }
        if (HasDifferentUv(vertex.U, vertex.V, vertex.U2, vertex.V2))
        {
            return (vertex.U2, vertex.V2);
        }

        return (vertex.U, vertex.V);
    }

    private static bool HasDifferentUv(float u1, float v1, float u2, float v2)
    {
        return MathF.Abs(u1 - u2) + MathF.Abs(v1 - v2) > 0.0001f;
    }

    private static bool IsNullPreviewMaterial(SubmeshData submesh)
    {
        return IsColorNull(submesh.MaterialName)
            || (submesh.TextureNames.TryGetValue("diffuse", out var diffuse) && IsColorNull(diffuse));
    }

    private static bool IsColorNull(string? textureName)
    {
        return string.Equals(NormalizeTextureName(textureName), "color_000", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTextureName(string? textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName))
        {
            return "";
        }

        var stem = Path.GetFileNameWithoutExtension(textureName);
        while (stem.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        return stem;
    }

    private static PointF Project(Vector3 p, Matrix4x4 transform, float scale, PointF center)
    {
        var t = Vector3.Transform(p, transform);
        return Project(t, scale, center);
    }

    private static PointF Project(Vector3 transformed, float scale, PointF center)
    {
        return new PointF(center.X + transformed.X * scale, center.Y - transformed.Y * scale);
    }

    private static Color ShadeColor(Color baseColor, float shade)
    {
        return Color.FromArgb(
            baseColor.A,
            Math.Clamp((int)(baseColor.R * shade), 0, 255),
            Math.Clamp((int)(baseColor.G * shade), 0, 255),
            Math.Clamp((int)(baseColor.B * shade), 0, 255));
    }

    private static int ShadeTexture(int argb, float shade)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        if (a < 8)
        {
            return Color.Transparent.ToArgb();
        }

        // Brilho do preview: Ambient levanta as sombras (piso de luz) e Gain clareia tudo. Bem altos
        // para um look "chapado"/toon parecido com o do jogo (quase albedo puro), sem ficar escuro.
        // Gain > 1 can push highlights all the way to white.
        const float ambient = 0.55f;
        const float gain = 1.20f;
        var lit = (ambient + shade * (1f - ambient)) * gain;
        return Color.FromArgb(
            a,
            Math.Clamp((int)(r * lit), 0, 255),
            Math.Clamp((int)(g * lit), 0, 255),
            Math.Clamp((int)(b * lit), 0, 255)).ToArgb();
    }

    private static int ApplyVertexColor(int argb, float tintR, float tintG, float tintB, float tintA)
    {
        if (tintR + tintG + tintB <= 0.001f)
        {
            tintR = 1f;
            tintG = 1f;
            tintB = 1f;
        }

        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return Color.FromArgb(
            Math.Clamp((int)(a * tintA), 0, 255),
            Math.Clamp((int)(r * tintR), 0, 255),
            Math.Clamp((int)(g * tintG), 0, 255),
            Math.Clamp((int)(b * tintB), 0, 255)).ToArgb();
    }

    private static float SampleNormalBoost(TextureImage? normal, float u, float v)
    {
        if (normal is null)
        {
            return 1f;
        }

        var argb = normal.Sample(u, v);
        var r = ((argb >> 16) & 0xFF) / 255f * 2f - 1f;
        var g = ((argb >> 8) & 0xFF) / 255f * 2f - 1f;
        var b = (argb & 0xFF) / 255f * 2f - 1f;
        var n = new Vector3(r, g, b);
        if (n.LengthSquared() < 0.0001f)
        {
            return 1f;
        }

        n = Vector3.Normalize(n);
        // High floor (0.88): the normal map adds subtle relief without darkening skin/clothes too much.
        return Math.Clamp(0.92f + n.Z * 0.18f + n.X * 0.04f + n.Y * 0.04f, 0.88f, 1.12f);
    }

    // Toon-style line layer in the preview. GLB/GLTF keeps it separate from the diffuse texture.
    private static int ApplyDetail(int baseArgb, TextureImage? detail, float u, float v)
    {
        return detail is null ? baseArgb : DetailCompositor.Apply(baseArgb, detail, u, v);
    }

    private static int ApplyBake(int baseArgb, TextureImage? bake, float u, float v)
    {
        if (bake is null)
        {
            return baseArgb;
        }

        var bakeArgb = bake.SampleClamped(u, v);
        var br = ((bakeArgb >> 16) & 0xFF) / 255f;
        var bg = ((bakeArgb >> 8) & 0xFF) / 255f;
        var bb = (bakeArgb & 0xFF) / 255f;
        var a = (baseArgb >> 24) & 0xFF;
        var r = (baseArgb >> 16) & 0xFF;
        var g = (baseArgb >> 8) & 0xFF;
        var b = baseArgb & 0xFF;
        return Color.FromArgb(
            a,
            Math.Clamp((int)(r * (0.72f + br * 0.38f)), 0, 255),
            Math.Clamp((int)(g * (0.72f + bg * 0.38f)), 0, 255),
            Math.Clamp((int)(b * (0.72f + bb * 0.38f)), 0, 255)).ToArgb();
    }

    private static int ApplyShadow(int baseArgb, TextureImage? shadow, float u, float v)
    {
        if (shadow is null)
        {
            return baseArgb;
        }

        var shadowArgb = shadow.SampleClamped(u, v);
        var alpha = ((shadowArgb >> 24) & 0xFF) / 255f;
        var factor = 0.78f + alpha * 0.22f;
        var a = (baseArgb >> 24) & 0xFF;
        var r = (baseArgb >> 16) & 0xFF;
        var g = (baseArgb >> 8) & 0xFF;
        var b = baseArgb & 0xFF;
        return Color.FromArgb(a, (int)(r * factor), (int)(g * factor), (int)(b * factor)).ToArgb();
    }

    private readonly record struct MeshBounds(Vector3 Center, float Radius);
    private readonly record struct RenderVertex(
        float X,
        float Y,
        float Z,
        float Shade,
        float U,
        float V,
        float DetailU,
        float DetailV,
        float BakeU,
        float BakeV,
        float ShadowU,
        float ShadowV,
        float ColorR,
        float ColorG,
        float ColorB,
        float ColorA);
}
