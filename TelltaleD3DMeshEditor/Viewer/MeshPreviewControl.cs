using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Texture;

namespace TelltaleD3DMeshEditor.Viewer;

public enum PreviewCameraMode
{
    Orbit,
    Flight,
}

public enum PreviewRenderMode
{
    Shaded,
    Unlit,
    NoTexture,
    UvView,
    TextureSlotDebug,
    Normals,
    VertexColor,
    SkinWeights,
}

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
    // Adjacent triangles can land a tiny fraction of a pixel apart after animated skinning and camera
    // projection. Accept a narrow shared-edge margin so floating-point rounding cannot expose the
    // transparent framebuffer as short light lines. Depth testing still decides the visible surface.
    private const float EdgeCoverageTolerance = 0.0015f;

    private MeshData? _mesh;
    private SkeletonData? _skeleton;
    private IReadOnlyDictionary<int, MaterialTextureSet> _textures = new Dictionary<int, MaterialTextureSet>();
    private MeshBounds _bounds;
    private bool _hasBounds;
    private string _sizeInfo = "";
    private int _partCount;
    private int _textureCount;
    private int[] _pixelBuffer = [];
    private float[] _depthBuffer = [];
    private TextureProbeHit[] _textureProbeHitBuffer = [];
    private Bitmap? _meshBitmap;
    private Point _lastMouse;
    private Point _textureProbeMouse;
    private float _yaw = DefaultYaw;
    private float _pitch = DefaultPitch;
    private float _zoom = DefaultZoom;
    private Vector2 _pan;
    private bool _showSkeleton;
    private bool _showFaces = true;
    private bool _showPolygons;
    private bool _panMode;
    private bool _poseMode;
    private bool _textureProbeEnabled;
    private bool _textureProbeLiveHover;
    private bool _textureProbeMouseInside;
    private bool _showDragDropHint;
    private Image? _dragDropImage;
    private Image? _emptyBackgroundImage;
    private int _selectedBone = -1;
    private readonly Dictionary<int, Vector3> _boneOffsets = new();
    private readonly Dictionary<int, Quaternion> _boneRotations = new();
    private Dictionary<int, int>? _rigidBoneMap;
    private bool _antiAliasing;
    private PreviewCameraMode _cameraMode = PreviewCameraMode.Orbit;
    private PreviewRenderMode _renderMode = PreviewRenderMode.Shaded;
    private Vector3 _flightPosition;
    private TextureProbeHit? _lockedTextureProbeHit;
    private int _textureProbeLayerIndex;

    public MeshPreviewControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(122, 122, 120);
        ForeColor = Color.Gainsboro;
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void SetScene(MeshData? mesh, SkeletonData? skeleton, IReadOnlyDictionary<int, MaterialTextureSet>? textures = null, int? partCount = null)
    {
        _mesh = mesh;
        _skeleton = skeleton;
        _textures = textures ?? new Dictionary<int, MaterialTextureSet>();
        _bounds = mesh is null ? default : ComputeBounds(mesh);
        _hasBounds = mesh is not null;
        _sizeInfo = mesh is null ? "" : BuildSizeInfo(mesh);
        _partCount = mesh is null ? 0 : Math.Max(1, partCount ?? 1);
        _textureCount = mesh is null ? 0 : _textures.Values.Sum(set => set.Count);
        _boneOffsets.Clear();
        _boneRotations.Clear();
        _rigidBoneMap = null;
        _selectedBone = -1;
        _lockedTextureProbeHit = null;
        _textureProbeLayerIndex = 0;
        // Reset the camera to the default view so the new model does not inherit the previous orientation.
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        _flightPosition = Vector3.Zero;
        Fit();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _meshBitmap?.Dispose();
            _dragDropImage?.Dispose();
            _emptyBackgroundImage?.Dispose();
        }

        base.Dispose(disposing);
    }

    public void Fit()
    {
        _zoom = DefaultZoom;
        _pan = Vector2.Zero;
        _flightPosition = Vector3.Zero;
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

    public void SetAntiAliasing(bool enabled)
    {
        if (_antiAliasing == enabled)
        {
            return;
        }

        _antiAliasing = enabled;
        _meshBitmap?.Dispose();
        _meshBitmap = null;
        Invalidate();
    }

    public void SetCameraMode(PreviewCameraMode mode)
    {
        if (_cameraMode == mode)
        {
            return;
        }

        _cameraMode = mode;
        _panMode = false;
        Cursor = _poseMode ? Cursors.Cross : Cursors.Default;
        Invalidate();
    }

    public void SetRenderMode(PreviewRenderMode mode)
    {
        if (_renderMode == mode)
        {
            return;
        }

        _renderMode = mode;
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

    public void SetTextureProbeEnabled(bool enabled)
    {
        if (_textureProbeEnabled == enabled)
        {
            return;
        }

        _textureProbeEnabled = enabled;
        _lockedTextureProbeHit = null;
        _textureProbeLayerIndex = 0;
        Cursor = _poseMode ? Cursors.Cross : _panMode ? Cursors.SizeAll : Cursors.Default;
        Invalidate();
    }

    public void SetTextureProbeLiveHover(bool enabled)
    {
        if (_textureProbeLiveHover == enabled)
        {
            return;
        }

        _textureProbeLiveHover = enabled;
        if (enabled)
        {
            _lockedTextureProbeHit = null;
        }

        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _textureProbeMouseInside = true;
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _textureProbeMouseInside = false;
        if (_textureProbeEnabled)
        {
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    public void SetDragDropHintVisible(bool visible)
    {
        if (_showDragDropHint == visible)
        {
            return;
        }

        _showDragDropHint = visible;
        Invalidate();
    }

    public void ResetPose()
    {
        _boneOffsets.Clear();
        _boneRotations.Clear();
        _selectedBone = -1;
        Invalidate();
    }

    // The skeleton currently shown, so external tools (e.g. the animation player) can map
    // animation bone CRC64s onto the same bones the preview deforms.
    public SkeletonData? CurrentSkeleton => _skeleton;

    // Applies one animation frame as a local pose per bone (keyed by bone CRC64).
    // Absolute mode: translations are in Telltale animation space (unit-length, scaled here by the
    // bone's rest-position length, mirroring the GLB exporter) and rotations REPLACE the rest pose.
    // Additive mode (Telltale "_add" animations): values are DELTAS layered on top of the rest pose
    // — applying them as absolutes would collapse the model onto its bone origins.
    // Bones not present in the pose keep their rest transform.
    public void ApplyAnimationLocalPose(
        IReadOnlyDictionary<ulong, (Vector3? Translation, Quaternion? Rotation)> pose,
        bool additive = false)
    {
        if (_skeleton is null)
        {
            return;
        }

        _boneOffsets.Clear();
        _boneRotations.Clear();
        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            var bone = _skeleton.Bones[i];
            if (!pose.TryGetValue(bone.Hash, out var local))
            {
                continue;
            }

            if (local.Translation is { } translation)
            {
                var decodedTranslation = DecodeAnimationTranslation(bone, translation);

                _boneOffsets[i] = additive
                    ? decodedTranslation
                    : decodedTranslation - new Vector3(bone.X, bone.Y, bone.Z);
            }

            if (local.Rotation is { } rotation)
            {
                if (additive)
                {
                    // BuildBoneWorldMatrix composes extraRotation * rest, which is exactly how an
                    // additive delta layers on top of the rest rotation.
                    _boneRotations[i] = Quaternion.Normalize(rotation);
                }
                else
                {
                    var rest = new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw);
                    rest = rest.LengthSquared() < 0.000001f ? Quaternion.Identity : Quaternion.Normalize(rest);
                    // Solve extra so extra * rest equals the animated local rotation.
                    _boneRotations[i] = Quaternion.Normalize(rotation * Quaternion.Inverse(rest));
                }
            }
        }

        Invalidate();
    }

    // Telltale stores animated translations in a normalized per-bone coordinate system. The engine
    // reconstructs them with both the local-position length and a direction adjustment derived from
    // mAnimTranslationScale (SkeletonInstance::_ApplySkeletonInstanceRestPose/_UpdateNode). Applying
    // only the length is almost invisible on regular body bones, but it sends Batman's many cape
    // control bones in different directions and stretches the cloth into long triangles.
    private static Vector3 DecodeAnimationTranslation(BoneData bone, Vector3 encoded)
    {
        var local = new Vector3(bone.X, bone.Y, bone.Z);
        var restLength = local.Length();
        if (restLength < 1e-8f)
        {
            return encoded;
        }

        var scale = bone.AnimTranslationScale;
        var safeScale = new Vector3(
            MathF.Abs(scale.X) > 1e-8f ? scale.X : 1f,
            MathF.Abs(scale.Y) > 1e-8f ? scale.Y : 1f,
            MathF.Abs(scale.Z) > 1e-8f ? scale.Z : 1f);
        var animationDirection = Vector3.Normalize(new Vector3(
            local.X / safeScale.X,
            local.Y / safeScale.Y,
            local.Z / safeScale.Z));
        var adjustment = RotationBetween(local, animationDirection);
        return Vector3.Transform(encoded * restLength, adjustment);
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var fromLength = from.Length();
        var toLength = to.Length();
        if (fromLength < 1e-8f || toLength < 1e-8f)
        {
            return Quaternion.Identity;
        }

        var first = from / fromLength;
        var second = to / toLength;
        var dot = Math.Clamp(Vector3.Dot(first, second), -1f, 1f);
        if (dot >= 0.99999f)
        {
            return Quaternion.Identity;
        }

        if (dot <= -0.99999f)
        {
            var basis = MathF.Abs(first.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var axis = Vector3.Normalize(Vector3.Cross(first, basis));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var rotationAxis = Vector3.Normalize(Vector3.Cross(first, second));
        return Quaternion.CreateFromAxisAngle(rotationAxis, MathF.Acos(dot));
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
        _textureProbeMouse = e.Location;
        if (_textureProbeEnabled && e.Button == MouseButtons.Left && TryGetTextureProbeHit(e.Location, out var probeHit))
        {
            var currentLayer = GetActiveTextureProbeLayerLabel();
            _lockedTextureProbeHit = probeHit;
            RestoreTextureProbeLayerIndex(probeHit, currentLayer);
            Invalidate();
            base.OnMouseDown(e);
            return;
        }

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
        if (_textureProbeEnabled && (_textureProbeLiveHover || _lockedTextureProbeHit is not null))
        {
            _textureProbeMouse = e.Location;
            Invalidate();
        }

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
            var lookSpeed = _cameraMode == PreviewCameraMode.Flight ? 0.0065f : 0.01f;
            _yaw += dx * lookSpeed;
            _pitch += dy * lookSpeed;
            _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
            _lastMouse = e.Location;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_textureProbeEnabled && (ModifierKeys & Keys.Control) != Keys.Control)
        {
            var hit = _lockedTextureProbeHit;
            if ((hit is null || _textureProbeLiveHover) && TryGetTextureProbeHit(e.Location, out var hoverHit))
            {
                hit = hoverHit;
            }

            var layers = hit is TextureProbeHit activeHit ? BuildTextureProbeLayers(activeHit) : [];
            if (layers.Count > 1)
            {
                _textureProbeLayerIndex = WrapIndex(_textureProbeLayerIndex + (e.Delta > 0 ? -1 : 1), layers.Count);
                Invalidate();
            }

            base.OnMouseWheel(e);
            return;
        }

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

    private bool IsPanGesture(MouseEventArgs e)
    {
        return e.Button == MouseButtons.Middle
            || (_panMode && e.Button == MouseButtons.Left)
            || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) == Keys.Shift);
    }

    private void ZoomAt(Point mouseLocation, float factor)
    {
        if (_cameraMode == PreviewCameraMode.Flight)
        {
            _zoom = Math.Clamp(_zoom * factor, 0.08f, 30f);
            Invalidate();
            return;
        }

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

    private bool MoveFlightCamera(Keys key)
    {
        if (_mesh is null)
        {
            return false;
        }

        var bounds = _hasBounds ? _bounds : ComputeBounds(_mesh);
        if (bounds.Radius <= 0)
        {
            return false;
        }

        var step = bounds.Radius * 0.08f / MathF.Max(_zoom, 0.1f);
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            step *= 3f;
        }
        else if ((ModifierKeys & Keys.Control) != 0)
        {
            step *= 0.35f;
        }

        var rotation = Matrix4x4.CreateRotationX(_pitch) * Matrix4x4.CreateRotationY(_yaw);
        if (!Matrix4x4.Invert(rotation, out var inverseRotation))
        {
            return false;
        }

        var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inverseRotation));
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverseRotation));
        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, inverseRotation));

        switch (key)
        {
            case Keys.W:
                _flightPosition += forward * step;
                _zoom = Math.Clamp(_zoom * 1.035f, 0.08f, 30f);
                return true;
            case Keys.S:
                _flightPosition -= forward * step;
                _zoom = Math.Clamp(_zoom * 0.966f, 0.08f, 30f);
                return true;
            case Keys.A:
                _flightPosition -= right * step;
                return true;
            case Keys.D:
                _flightPosition += right * step;
                return true;
            case Keys.Q:
                _flightPosition -= up * step;
                return true;
            case Keys.E:
                _flightPosition += up * step;
                return true;
            default:
                return false;
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return _cameraMode == PreviewCameraMode.Flight &&
               key is Keys.W or Keys.A or Keys.S or Keys.D or Keys.Q or Keys.E
            ? true
            : base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F || e.KeyCode == Keys.Home)
        {
            Fit();
            e.Handled = true;
            return;
        }

        if (_cameraMode == PreviewCameraMode.Flight && MoveFlightCamera(e.KeyCode))
        {
            e.Handled = true;
            Invalidate();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = _antiAliasing ? SmoothingMode.AntiAlias : SmoothingMode.None;
        DrawStudioBackground(g);

        if (_mesh is null)
        {
            if (!_showDragDropHint)
            {
                DrawEmptyPreviewBackground(g);
            }

            DrawEmptyPreview(g);
            return;
        }

        var bounds = _hasBounds ? _bounds : ComputeBounds(_mesh);
        if (bounds.Radius <= 0)
        {
            DrawCentered(g, "This model has no vertices to display");
            return;
        }

        var transform = BuildViewTransform(bounds);
        var scale = GetViewScale(bounds);
        var center = GetViewportCenter();

        if (_renderMode is PreviewRenderMode.Shaded or PreviewRenderMode.NoTexture)
        {
            DrawGroundShadow(g, bounds.Radius, scale, center);
        }

        DrawMesh(g, transform, scale, center);
        if (_showSkeleton && _skeleton is not null && _skeleton.Bones.Count > 0)
        {
            DrawSkeleton(g, transform, scale, center);
        }

        if (_textureProbeEnabled)
        {
            DrawTextureProbeOverlay(g);
        }

        using var textBrush = new SolidBrush(Color.FromArgb(230, 245, 245, 245));
        var fileInfo = $"{_mesh.Name}  |  version: {_mesh.Version}";
        if (_partCount > 1)
        {
            fileInfo += $"  parts: {_partCount}";
        }

        fileInfo += $"  textures: {_textureCount}";
        var geometryInfo = $"submeshes: {_mesh.Submeshes.Count}  vertices: {_mesh.VertexCount}  polygons: {_mesh.FaceCount}";
        if (_skeleton is not null)
        {
            geometryInfo += $"  bones: {_skeleton.Bones.Count}";
        }

        g.DrawString(fileInfo, Font, textBrush, 10, 10);
        g.DrawString(geometryInfo, Font, textBrush, 10, 28);
        if (_sizeInfo.Length > 0)
        {
            g.DrawString(_sizeInfo, Font, textBrush, 10, 46);
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

    private TextureProbeHit[]? PrepareTextureProbeHitBuffer(int width, int height)
    {
        if (!_textureProbeEnabled)
        {
            return null;
        }

        var pixelCount = checked(width * height);
        if (_textureProbeHitBuffer.Length != pixelCount)
        {
            _textureProbeHitBuffer = new TextureProbeHit[pixelCount];
        }

        Array.Fill(_textureProbeHitBuffer, TextureProbeHit.Empty, 0, pixelCount);
        return _textureProbeHitBuffer;
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

        var sample = _antiAliasing ? 2 : 1;
        var width = checked(viewportWidth * sample);
        var height = checked(viewportHeight * sample);
        var renderScale = scale * sample;
        var renderCenter = new PointF(center.X * sample, center.Y * sample);
        var (pixels, depth, bitmap) = PrepareRasterBuffers(width, height);
        var probeHits = PrepareTextureProbeHitBuffer(width, height);
        var light = Vector3.Normalize(new Vector3(-0.45f, -0.65f, 1f));
        var baseBoneMatrices = _skeleton is not null ? BuildBoneWorldMatrices(_skeleton, null, null) : null;
        var hasPose = _boneOffsets.Count > 0 || _boneRotations.Count > 0;
        var posedBoneMatrices = _skeleton is not null && hasPose ? BuildBoneWorldMatrices(_skeleton, _boneOffsets, _boneRotations) : baseBoneMatrices;

        for (var pass = 0; pass < 2; pass++)
        {
            var transparentPass = pass == 1;
            var submeshOrder = Enumerable.Range(0, _mesh!.Submeshes.Count);
            if (transparentPass)
            {
                submeshOrder = submeshOrder
                    .OrderBy(index => EstimateSubmeshDepth(_mesh.Submeshes[index], transform))
                    .ToList();
            }
            else if (GameConfig.Current.Id == GameId.TalesFromTheBorderlandsE3)
            {
                submeshOrder = submeshOrder
                    .OrderBy(index => IsTftbE3UiStrokeUnderlay(_mesh.Submeshes[index]) ? 0 : 1)
                    .ThenBy(index => index)
                    .ToList();
            }

            foreach (var submeshIndex in submeshOrder)
            {
                var submesh = _mesh.Submeshes[submeshIndex];
                if (IsNullPreviewMaterial(submesh))
                {
                    continue;
                }

                _textures.TryGetValue(submeshIndex, out var textures);
                if (IsBatmanEyeLensMaterial(submesh) &&
                    textures?.Diffuse is null &&
                    _renderMode is PreviewRenderMode.Shaded or PreviewRenderMode.Unlit)
                {
                    // Batman's eyeball has a separate cornea/specular shell. Some extracted sets do
                    // not contain its diffuse texture; drawing the fallback grey material produces
                    // an artificial milky film over the iris, so leave that unsupported shell clear.
                    continue;
                }

                var transparentMaterial = UsesTextureAlpha(_renderMode) && IsTransparentPreviewMaterial(_mesh.Name, submesh, textures);
                if (transparentMaterial != transparentPass)
                {
                    continue;
                }

                var boneMap = BuildBoneMap(submesh);
                var rigidPose = BuildRigidPoseMatrix(submesh, boneMap, baseBoneMatrices, posedBoneMatrices);
                var renderVertices = new RenderVertex[submesh.Vertices.Count];
                for (var i = 0; i < submesh.Vertices.Count; i++)
                {
                    var vertex = submesh.Vertices[i];
                    var posed = ApplyPose(vertex, rigidPose, boneMap, baseBoneMatrices, posedBoneMatrices);
                    var view = Vector3.Transform(posed, transform);
                    var screen = Project(view, renderScale, renderCenter);
                    var posedNormal = ApplyPoseNormal(vertex, rigidPose);
                    var normal = Vector3.TransformNormal(posedNormal, transform);
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
                    var debugColor = BuildDebugVertexColor(vertex, posedNormal, submesh, submeshIndex, boneMap, _renderMode);
                    renderVertices[i] = new RenderVertex(
                        screen.X, screen.Y, view.Z, shade,
                        vertex.U, vertex.V, detailU, detailV, bakeU, bakeV, shadowU, shadowV,
                        vertex.ColorR, vertex.ColorG, vertex.ColorB, vertex.ColorA,
                        debugColor.R, debugColor.G, debugColor.B, debugColor.A);
                }

                var writeDepth = !transparentPass && !IsTftbE3UiStrokeUnderlay(submesh);
                var useTextureAlpha = UsesTextureAlpha(_renderMode);
                var forceOpaqueTextureAlpha = useTextureAlpha && ShouldForceOpaqueTextureAlpha(_mesh.Name, submesh, textures);
                var forceDarkAlphaTexture = useTextureAlpha && ShouldForceDarkAlphaTexture(submesh, textures);
                var forceGlassAlpha = useTextureAlpha &&
                                      transparentPass &&
                                      IsBatmanGlassMaterial(submesh) &&
                                      !IsBatmanEyeLensMaterial(submesh);
                var alphaCutoutThreshold = useTextureAlpha ? GetAlphaCutoutThreshold(submesh, textures) : 0;
                RasterizeFaces(
                    renderVertices,
                    submesh.Faces,
                    textures,
                    pixels,
                    depth,
                    probeHits,
                    submeshIndex,
                    width,
                    height,
                    writeDepth,
                    forceOpaqueTextureAlpha,
                    forceDarkAlphaTexture,
                    forceGlassAlpha,
                    alphaCutoutThreshold,
                    _renderMode);
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

        if (sample == 1)
        {
            g.DrawImageUnscaled(bitmap, 0, 0);
            return;
        }

        var previousInterpolation = g.InterpolationMode;
        var previousPixelOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(bitmap, new Rectangle(0, 0, viewportWidth, viewportHeight));
        g.InterpolationMode = previousInterpolation;
        g.PixelOffsetMode = previousPixelOffset;
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
            var rigidPose = BuildRigidPoseMatrix(submesh, boneMap, baseBoneMatrices, posedBoneMatrices);
            var points = new PointF[submesh.Vertices.Count];
            for (var i = 0; i < submesh.Vertices.Count; i++)
            {
                points[i] = Project(ApplyPose(submesh.Vertices[i], rigidPose, boneMap, baseBoneMatrices, posedBoneMatrices), transform, scale, center);
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

    private static bool IsTransparentPreviewMaterial(string meshName, SubmeshData submesh, MaterialTextureSet? textures)
    {
        if (ShouldForceOpaqueTextureAlpha(meshName, submesh, textures) ||
            ShouldUseAlphaCutoutDepth(submesh, textures))
        {
            return false;
        }

        var diffuse = textures?.Diffuse;
        return HasPreviewVertexAlpha(submesh) ||
               IsBatmanGlassMaterial(submesh) ||
               IsBatmanPictureFrameGlassMaterial(meshName, submesh) ||
               (diffuse is not null &&
                (diffuse.AverageAlpha < 0.95f || diffuse.NonOpaqueAlphaRatio > 0.08f));
    }

    // Batman eyeglass lenses (and similar glass) ship an opaque diffuse but should read as transparent.
    // Detected by the lens/glass naming on the submesh, material or diffuse texture.
    private static bool IsBatmanGlassMaterial(SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.Batman)
        {
            return false;
        }

        // Match "lens" only (not "glasses"): the combined submesh name embeds the part stem, so matching
        // "glasses" would also turn the opaque frame transparent. The lens diffuse/material carries "lens".
        static bool Match(string? name) => !string.IsNullOrWhiteSpace(name) &&
            name.Contains("lens", StringComparison.OrdinalIgnoreCase);

        return Match(submesh.Name) || Match(submesh.MaterialName) ||
               (submesh.TextureNames.TryGetValue("diffuse", out var d) && Match(d));
    }

    private static bool IsBatmanEyeLensMaterial(SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.Batman)
        {
            return false;
        }

        static bool Match(string? name) => !string.IsNullOrWhiteSpace(name) &&
            name.Contains("eyelens", StringComparison.OrdinalIgnoreCase);

        return Match(submesh.Name) ||
               Match(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(Match);
    }

    private static bool IsBatmanEyeballMaterial(SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.Batman || IsBatmanEyeLensMaterial(submesh))
        {
            return false;
        }

        static bool Match(string? name)
        {
            var normalized = NormalizeTextureName(name);
            return normalized.Contains("sharedparts_eyes", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.Contains("eyelash", StringComparison.OrdinalIgnoreCase);
        }

        return Match(submesh.Name) ||
               Match(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(Match);
    }

    private static bool ShouldForceOpaqueTextureAlpha(string meshName, SubmeshData submesh, MaterialTextureSet? textures)
    {
        if (textures?.Diffuse is null)
        {
            return false;
        }

        if (IsBatmanPictureFrameGlassMaterial(meshName, submesh))
        {
            // Its nearly uniform low alpha is intentional translucency. Do not let the generic
            // packed-data detector below turn the glass plane or broken shards opaque.
            return false;
        }

        if (IsBatmanPictureFrameBaseMaterial(meshName, submesh))
        {
            // This atlas stores reflectivity/material data in alpha. Applying it as coverage made
            // the wooden frame, photograph and backing disappear, while the separate glass piece
            // is identified by its mesh/submesh role below.
            return true;
        }

        if (IsBatmanEyeballMaterial(submesh))
        {
            // The alpha in Batman's shared eye atlas is not transparency for the eyeball surface.
            // Keeping it opaque prevents holes or a washed-out iris while eyelashes remain separate.
            return true;
        }

        // Batman's newer engine packs a shading term (gloss/scatter) into the diffuse alpha of skin
        // materials instead of opacity: Bruce Wayne's face and hands average ~0.65 alpha with no fully
        // opaque pixel anywhere. Treating that as coverage rendered faces and hands see-through. The
        // check is on content, so genuinely masked materials (hair, lenses) keep their transparency.
        if (GameConfig.Current.Id == GameId.Batman)
        {
            return textures.Diffuse.HasPackedDataAlpha;
        }

        if (GameConfig.Current.Id != GameId.GameOfThrones)
        {
            return false;
        }

        return IsGameOfThronesCharacterPreviewMaterial(submesh, textures) &&
               !IsGameOfThronesTransparentPreviewMaterial(submesh, textures);
    }

    private static bool IsBatmanPictureFrameBaseMaterial(string meshName, SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.Batman ||
            IsBatmanPictureFrameGlassMaterial(meshName, submesh))
        {
            return false;
        }

        static bool MatchBase(string? value)
        {
            var name = NormalizeTextureName(value);
            return name.Equals("obj_pictureFrameBreakableWayne", StringComparison.OrdinalIgnoreCase);
        }

        return MatchBase(submesh.Name) ||
               MatchBase(submesh.MaterialName) ||
               (submesh.TextureNames.TryGetValue("diffuse", out var diffuse) && MatchBase(diffuse));
    }

    private static bool IsBatmanPictureFrameGlassMaterial(string meshName, SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.Batman)
        {
            return false;
        }

        static bool IsPictureFrame(string? value)
            => value is not null &&
               value.Contains("pictureFrame", StringComparison.OrdinalIgnoreCase);

        static bool IsBrokenGlass(string? value)
            => value is not null &&
               IsPictureFrame(value) &&
               value.Contains("BrokenGlass", StringComparison.OrdinalIgnoreCase);

        var unbrokenGlassAsset = IsPictureFrame(meshName) &&
                                 meshName.Contains("glassUnbroken", StringComparison.OrdinalIgnoreCase);
        return unbrokenGlassAsset ||
               IsBrokenGlass(submesh.Name) ||
               IsBrokenGlass(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsBrokenGlass);
    }

    private static bool IsGameOfThronesCharacterPreviewMaterial(SubmeshData submesh, MaterialTextureSet textures)
    {
        return IsGameOfThronesCharacterPreviewName(submesh.Name) ||
               IsGameOfThronesCharacterPreviewName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsGameOfThronesCharacterPreviewName) ||
               IsGameOfThronesCharacterPreviewName(Path.GetFileNameWithoutExtension(textures.Diffuse?.SourcePath));
    }

    private static bool IsGameOfThronesCharacterPreviewName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.StartsWith("sk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameOfThronesTransparentPreviewMaterial(SubmeshData submesh, MaterialTextureSet textures)
    {
        return IsGameOfThronesBlendPreviewName(submesh.Name) ||
               IsGameOfThronesBlendPreviewName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsGameOfThronesBlendPreviewName) ||
               IsGameOfThronesBlendPreviewName(Path.GetFileNameWithoutExtension(textures.Diffuse?.SourcePath));
    }

    private static bool IsGameOfThronesBlendPreviewName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("lens", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
               IsEyelashPreviewName(name) ||
               name.Contains("alpha", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldForceDarkAlphaTexture(SubmeshData submesh, MaterialTextureSet? textures)
    {
        if (GameConfig.Current.Id != GameId.GameOfThrones || textures?.Diffuse is null)
        {
            return false;
        }

        return IsEyelashPreviewName(submesh.Name) ||
               IsEyelashPreviewName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsEyelashPreviewName) ||
               IsEyelashPreviewName(Path.GetFileNameWithoutExtension(textures.Diffuse.SourcePath));
    }

    private static bool IsEyelashPreviewName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("eyelash", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("eyelashes", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("lashes", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetAlphaCutoutThreshold(SubmeshData submesh, MaterialTextureSet? textures)
    {
        if (ShouldUseAlphaCutoutDepth(submesh, textures))
        {
            return 32;
        }

        if (GameConfig.Current.Id != GameId.GameOfThrones || textures?.Diffuse is null)
        {
            return 0;
        }

        return IsGameOfThronesCharacterPreviewMaterial(submesh, textures) && IsHairPreviewMaterial(submesh, textures) ? 96 : 0;
    }

    private static bool ShouldUseAlphaCutoutDepth(SubmeshData submesh, MaterialTextureSet? textures)
    {
        return GameConfig.Current.Id == GameId.WalkingDeadMichonne &&
               textures?.Diffuse is not null &&
               IsMichonneCharacterPreviewMaterial(submesh, textures);
    }

    private static bool IsMichonneCharacterPreviewMaterial(SubmeshData submesh, MaterialTextureSet textures)
    {
        return IsMichonneCharacterPreviewName(submesh.Name) ||
               IsMichonneCharacterPreviewName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsMichonneCharacterPreviewName) ||
               IsMichonneCharacterPreviewName(Path.GetFileNameWithoutExtension(textures.Diffuse?.SourcePath));
    }

    private static bool IsMichonneCharacterPreviewName(string? name)
    {
        var normalized = NormalizeTextureName(name);
        return normalized.StartsWith("sk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHairPreviewMaterial(SubmeshData submesh, MaterialTextureSet textures)
    {
        return IsHairPreviewName(submesh.Name) ||
               IsHairPreviewName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsHairPreviewName) ||
               IsHairPreviewName(Path.GetFileNameWithoutExtension(textures.Diffuse?.SourcePath));
    }

    private static bool IsHairPreviewName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.Contains("hair", StringComparison.OrdinalIgnoreCase);

    private static bool IsTftbE3UiStrokeUnderlay(SubmeshData submesh)
    {
        if (GameConfig.Current.Id != GameId.TalesFromTheBorderlandsE3)
        {
            return false;
        }

        return IsUiStrokeName(submesh.Name) ||
               IsUiStrokeName(submesh.MaterialName) ||
               submesh.TextureNames.Values.Any(IsUiStrokeName);
    }

    private static bool IsUiStrokeName(string? name)
    {
        var normalized = NormalizeTextureName(name);
        return normalized.StartsWith("ui_", StringComparison.OrdinalIgnoreCase) &&
               normalized.Contains("stroke", StringComparison.OrdinalIgnoreCase);
    }

    private static float EstimateSubmeshDepth(SubmeshData submesh, Matrix4x4 transform)
    {
        if (submesh.Vertices.Count == 0)
        {
            return 0f;
        }

        var sum = 0f;
        foreach (var vertex in submesh.Vertices)
        {
            sum += Vector3.Transform(new Vector3(vertex.X, vertex.Y, vertex.Z), transform).Z;
        }

        return sum / submesh.Vertices.Count;
    }

    private static void RasterizeFaces(
        RenderVertex[] vertices,
        IReadOnlyList<(int A, int B, int C)> faces,
        MaterialTextureSet? textures,
        int[] pixels,
        float[] depth,
        TextureProbeHit[]? probeHits,
        int submeshIndex,
        int width,
        int height,
        bool writeDepth,
        bool forceOpaqueTextureAlpha,
        bool forceDarkAlphaTexture,
        bool forceGlassAlpha,
        int alphaCutoutThreshold,
        PreviewRenderMode renderMode)
    {
        void RasterizeBand(int clipMinY, int clipMaxY)
        {
            foreach (var (a, b, c) in faces)
            {
                if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length)
                {
                    continue;
                }

                RasterizeTriangle(
                    vertices[a],
                    vertices[b],
                    vertices[c],
                    textures,
                    pixels,
                    depth,
                    probeHits,
                    submeshIndex,
                    width,
                    height,
                    clipMinY,
                    clipMaxY,
                    writeDepth,
                    forceOpaqueTextureAlpha,
                    forceDarkAlphaTexture,
                    forceGlassAlpha,
                    alphaCutoutThreshold,
                    renderMode);
            }
        }

        // Each worker owns complete scanlines, so z-buffering, transparency, texture sampling and
        // draw order remain byte-for-byte equivalent without locks. Small batches stay sequential
        // because task scheduling would cost more than their rasterization.
        var processorCount = Environment.ProcessorCount;
        var bandCount = Math.Min(processorCount, Math.Max(1, (height + 95) / 96));
        if (processorCount <= 1 || bandCount <= 1 || faces.Count < 128)
        {
            RasterizeBand(0, height - 1);
            return;
        }

        var bandHeight = (height + bandCount - 1) / bandCount;
        Parallel.For(
            0,
            bandCount,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, processorCount - 1) },
            band =>
            {
                var clipMinY = band * bandHeight;
                var clipMaxY = Math.Min(height - 1, clipMinY + bandHeight - 1);
                if (clipMinY <= clipMaxY)
                {
                    RasterizeBand(clipMinY, clipMaxY);
                }
            });
    }

    private static void RasterizeTriangle(
        RenderVertex a,
        RenderVertex b,
        RenderVertex c,
        MaterialTextureSet? textures,
        int[] pixels,
        float[] depth,
        TextureProbeHit[]? probeHits,
        int submeshIndex,
        int width,
        int height,
        int clipMinY,
        int clipMaxY,
        bool writeDepth,
        bool forceOpaqueTextureAlpha,
        bool forceDarkAlphaTexture,
        bool forceGlassAlpha,
        int alphaCutoutThreshold,
        PreviewRenderMode renderMode)
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
        var minY = Math.Clamp((int)MathF.Floor(rawMinY), clipMinY, clipMaxY);
        var maxY = Math.Clamp((int)MathF.Ceiling(rawMaxY), clipMinY, clipMaxY);
        if (minY > maxY || rawMaxY < clipMinY || rawMinY > clipMaxY)
        {
            return;
        }
        var baseColor = Color.FromArgb(255, 226, 229, 229);
        var invArea = 1f / area;
        var w0StepX = (c.Y - b.Y) * invArea;
        var w1StepX = (a.Y - c.Y) * invArea;
        // Do not expand genuinely blended surfaces: overlapping coverage there would blend a shared
        // edge twice. Opaque/depth-writing geometry can use the wider, z-buffer-safe crack guard.
        var edgeTolerance = writeDepth ? EdgeCoverageTolerance : 0.0001f;

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
                if (w0 < -edgeTolerance ||
                    w1 < -edgeTolerance ||
                    w2 < -edgeTolerance)
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
                var u = a.U * w0 + b.U * w1 + c.U * w2;
                var v = a.V * w0 + b.V * w1 + c.V * w2;
                var detailU = a.DetailU * w0 + b.DetailU * w1 + c.DetailU * w2;
                var detailV = a.DetailV * w0 + b.DetailV * w1 + c.DetailV * w2;
                var bakeU = a.BakeU * w0 + b.BakeU * w1 + c.BakeU * w2;
                var bakeV = a.BakeV * w0 + b.BakeV * w1 + c.BakeV * w2;
                var shadowU = a.ShadowU * w0 + b.ShadowU * w1 + c.ShadowU * w2;
                var shadowV = a.ShadowV * w0 + b.ShadowV * w1 + c.ShadowV * w2;

                if (renderMode == PreviewRenderMode.Shaded)
                {
                    if (textures?.Diffuse is null)
                    {
                        color = ShadeColor(baseColor, shade).ToArgb();
                        if (GameConfig.Current.Id == GameId.TalesFromTheBorderlandsE3 && textures?.Bake is not null)
                        {
                            color = ShadeTexture(textures.Bake.SampleClamped(bakeU, bakeV), shade);
                        }
                    }
                    else
                    {
                        var normalBoost = SampleNormalBoost(textures.Normal, u, v);
                        var detailBoost = SampleDetailNormalBoost(textures.Detail, detailU, detailV);
                        var diffuseSample = textures.Diffuse.Sample(u, v);
                        if (forceOpaqueTextureAlpha)
                        {
                            // Packed Batman alpha is shading data, not coverage. Make it opaque before
                            // ShadeTexture checks alpha; otherwise its rare near-zero data values are
                            // converted to Color.Transparent and become isolated pinholes in the preview.
                            diffuseSample |= unchecked((int)0xFF000000);
                        }
                        color = ShadeTexture(diffuseSample, shade * normalBoost * detailBoost);
                        color = ApplyDetail(color, textures.Detail, detailU, detailV);
                        color = ApplyBake(color, textures.Bake, bakeU, bakeV);
                        color = ApplyOcclusion(color, textures.Occlusion, u, v);
                        color = ApplyShadow(color, textures.Shadow, shadowU, shadowV);
                        if (forceDarkAlphaTexture)
                        {
                            color &= unchecked((int)0xFF000000);
                        }
                    }
                }
                else if (renderMode == PreviewRenderMode.Unlit)
                {
                    color = textures?.Diffuse is null
                        ? baseColor.ToArgb()
                        : ApplyDetail(textures.Diffuse.Sample(u, v), textures.Detail, detailU, detailV);
                }
                else if (renderMode == PreviewRenderMode.NoTexture)
                {
                    color = ShadeColor(baseColor, shade).ToArgb();
                }
                else if (renderMode == PreviewRenderMode.UvView)
                {
                    color = BuildUvDebugColor(u, v);
                }
                else
                {
                    var debugR = Math.Clamp(a.DebugR * w0 + b.DebugR * w1 + c.DebugR * w2, 0f, 1f);
                    var debugG = Math.Clamp(a.DebugG * w0 + b.DebugG * w1 + c.DebugG * w2, 0f, 1f);
                    var debugB = Math.Clamp(a.DebugB * w0 + b.DebugB * w1 + c.DebugB * w2, 0f, 1f);
                    var debugA = Math.Clamp(a.DebugA * w0 + b.DebugA * w1 + c.DebugA * w2, 0f, 1f);
                    color = Color.FromArgb(
                        Math.Clamp((int)MathF.Round(debugA * 255f), 0, 255),
                        Math.Clamp((int)MathF.Round(debugR * 255f), 0, 255),
                        Math.Clamp((int)MathF.Round(debugG * 255f), 0, 255),
                        Math.Clamp((int)MathF.Round(debugB * 255f), 0, 255)).ToArgb();
                }

                if (!forceOpaqueTextureAlpha &&
                    alphaCutoutThreshold > 0 &&
                    ((color >> 24) & 0xFF) < alphaCutoutThreshold)
                {
                    continue;
                }

                if (renderMode is PreviewRenderMode.Shaded or PreviewRenderMode.Unlit)
                {
                    color = ApplyVertexColor(color, vertexColorR, vertexColorG, vertexColorB, vertexColorA);
                }

                // Batman stores gloss/scatter in diffuse alpha and can also carry non-coverage vertex
                // alpha. Force opacity after vertex colour so that alpha cannot reopen isolated pinholes
                // across skin that the material declares solid.
                if (forceOpaqueTextureAlpha)
                {
                    color |= unchecked((int)0xFF000000);
                }

                // Glass/lens materials (e.g. Batman's eyeglasses) ship an opaque diffuse; the see-through
                // look comes from the material, so force a semi-transparent alpha here so the lens blends
                // over what is behind it instead of reading as a solid disc.
                if (forceGlassAlpha)
                {
                    color = (color & 0x00FFFFFF) | (110 << 24);
                }

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

                if (writeDepth)
                {
                    depth[index] = z;
                }

                if (probeHits is not null)
                {
                    probeHits[index] = new TextureProbeHit(submeshIndex, u, v, detailU, detailV, bakeU, bakeV, shadowU, shadowV);
                }

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

    private bool TryGetTextureProbeHit(Point location, out TextureProbeHit hit)
    {
        hit = default;
        if (!_textureProbeEnabled || _textureProbeHitBuffer.Length == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return false;
        }

        var sample = _antiAliasing ? 2 : 1;
        var width = ClientSize.Width * sample;
        var height = ClientSize.Height * sample;
        var x = location.X * sample;
        var y = location.Y * sample;
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return false;
        }

        var index = y * width + x;
        if ((uint)index >= _textureProbeHitBuffer.Length)
        {
            return false;
        }

        hit = _textureProbeHitBuffer[index];
        return hit.SubmeshIndex >= 0;
    }

    private void DrawTextureProbeOverlay(Graphics g)
    {
        if (!_textureProbeMouseInside || !TryGetTextureProbeHit(_textureProbeMouse, out var hoverHit))
        {
            return;
        }

        var hit = _textureProbeLiveHover ? hoverHit : _lockedTextureProbeHit;
        if (hit is not TextureProbeHit activeHit)
        {
            return;
        }

        var layers = BuildTextureProbeLayers(activeHit);
        if (layers.Count == 0)
        {
            return;
        }

        _textureProbeLayerIndex = Math.Clamp(_textureProbeLayerIndex, 0, layers.Count - 1);
        var layer = layers[_textureProbeLayerIndex];
        const int diameter = 138;
        const int gap = 18;
        var circleX = _textureProbeMouse.X + 26;
        var circleY = _textureProbeMouse.Y - diameter / 2;
        if (circleX + diameter + 128 > ClientSize.Width)
        {
            circleX = _textureProbeMouse.X - diameter - 26;
        }

        circleX = Math.Clamp(circleX, 8, Math.Max(8, ClientSize.Width - diameter - 8));
        circleY = Math.Clamp(circleY, 44, Math.Max(44, ClientSize.Height - diameter - 42));
        var circle = new Rectangle(circleX, circleY, diameter, diameter);
        var center = new Point(circle.Left + diameter / 2, circle.Top + diameter / 2);

        using var pointerPen = new Pen(Color.FromArgb(230, 45, 135, 255), 1.4f);
        using var pointBrush = new SolidBrush(Color.FromArgb(245, 45, 135, 255));
        g.DrawLine(pointerPen, _textureProbeMouse, new Point(circle.Left + 10, center.Y));
        g.FillEllipse(pointBrush, _textureProbeMouse.X - 3, _textureProbeMouse.Y - 3, 6, 6);

        DrawTextureProbeCircle(g, circle, layer);

        var labelSize = g.MeasureString(layer.Label, Font);
        var labelRect = new RectangleF(
            circle.Left + (circle.Width - labelSize.Width) * 0.5f - 10f,
            circle.Top - labelSize.Height - 12f,
            labelSize.Width + 20f,
            labelSize.Height + 6f);
        using var labelBack = new SolidBrush(Color.FromArgb(238, 248, 249, 252));
        using var labelBorder = new Pen(Color.FromArgb(185, 74, 92, 114));
        using var labelPath = RoundedRect(labelRect, 4f);
        using var blueBrush = new SolidBrush(Color.FromArgb(255, 36, 131, 255));
        g.FillPath(labelBack, labelPath);
        g.DrawPath(labelBorder, labelPath);
        g.DrawString(layer.Label, Font, blueBrush, labelRect.Left + 10f, labelRect.Top + 3f);

        DrawTextureProbeLayerList(g, layers, circle.Right + gap, circle.Top + 8);
        DrawTextureProbeName(g, layer.TextureName, circle);
    }

    private void DrawTextureProbeCircle(Graphics g, Rectangle circle, TextureProbeLayer layer)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(circle);
        var previousClip = g.Clip;
        g.SetClip(path);

        using (var bitmap = BuildTextureProbeBitmap(layer, circle.Width, circle.Height))
        {
            g.DrawImageUnscaled(bitmap, circle.Left, circle.Top);
        }

        g.Clip = previousClip;
        using var fill = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
        using var border = new Pen(Color.FromArgb(245, 45, 135, 255), 2f);
        using var inner = new Pen(Color.FromArgb(190, 255, 255, 255), 1f);
        g.FillEllipse(fill, circle);
        g.DrawEllipse(inner, circle.Left + 4, circle.Top + 4, circle.Width - 8, circle.Height - 8);
        g.DrawEllipse(border, circle);
    }

    private static Bitmap BuildTextureProbeBitmap(TextureProbeLayer layer, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var pixels = new int[width * height];
        var zoom = MathF.Max(2f, MathF.Min(8f, MathF.Min(layer.Texture.Width, layer.Texture.Height) / 32f));
        var swizzledNormal = layer.Kind == TextureProbeLayerKind.Normal && IsLikelyTelltaleSwizzledNormal(layer.Texture);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var u = layer.U + (x - width * 0.5f) / (width * zoom);
                var v = layer.V - (y - height * 0.5f) / (height * zoom);
                var sample = layer.Texture.Sample(u, v);
                pixels[y * width + x] = layer.Kind switch
                {
                    TextureProbeLayerKind.Normal => VisualizeNormalProbePixel(sample, swizzledNormal),
                    TextureProbeLayerKind.Detail => VisualizeDetailProbePixel(sample, layer.Texture),
                    TextureProbeLayerKind.Auxiliary => VisualizeAuxiliaryProbePixel(sample, layer.Texture),
                    _ => sample | unchecked((int)0xFF000000),
                };
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

        return bitmap;
    }

    private void DrawTextureProbeLayerList(Graphics g, IReadOnlyList<TextureProbeLayer> layers, int x, int y)
    {
        var panelWidth = MeasureTextureProbeLayerPanelWidth(g, layers);
        if (x + panelWidth > ClientSize.Width)
        {
            x = Math.Max(8, x - ((panelWidth + 18) * 2));
        }

        using var activeBrush = new SolidBrush(Color.FromArgb(255, 36, 131, 255));
        using var inactiveBrush = new SolidBrush(Color.FromArgb(245, 26, 32, 42));
        using var dotBrush = new SolidBrush(Color.FromArgb(235, 210, 216, 226));
        using var dotPen = new Pen(Color.FromArgb(235, 98, 108, 124));
        using var panelBrush = new SolidBrush(Color.FromArgb(178, 244, 246, 250));
        using var panelBorder = new Pen(Color.FromArgb(128, 122, 134, 152));

        var panelHeight = layers.Count * 24 + 14;
        var panelRect = new RectangleF(x - 10, y - 8, panelWidth, panelHeight);
        using (var panelPath = RoundedRect(panelRect, 5f))
        {
            g.FillPath(panelBrush, panelPath);
            g.DrawPath(panelBorder, panelPath);
        }

        for (var i = 0; i < layers.Count; i++)
        {
            var rowY = y + i * 24;
            if (i == _textureProbeLayerIndex)
            {
                g.FillEllipse(activeBrush, x, rowY + 4, 10, 10);
                g.DrawString(layers[i].Label, Font, activeBrush, x + 22, rowY);
            }
            else
            {
                g.FillEllipse(dotBrush, x + 2, rowY + 6, 7, 7);
                g.DrawEllipse(dotPen, x + 2, rowY + 6, 7, 7);
                g.DrawString(layers[i].Label, Font, inactiveBrush, x + 22, rowY);
            }
        }
    }

    private int MeasureTextureProbeLayerPanelWidth(Graphics g, IReadOnlyList<TextureProbeLayer> layers)
    {
        var maxTextWidth = 88f;
        foreach (var layer in layers)
        {
            maxTextWidth = MathF.Max(maxTextWidth, g.MeasureString(layer.Label, Font).Width);
        }

        return Math.Clamp((int)MathF.Ceiling(maxTextWidth + 44f), 118, 190);
    }

    private void DrawTextureProbeName(Graphics g, string textureName, Rectangle circle)
    {
        var maxWidth = Math.Max(160, Math.Min(ClientSize.Width - 24, 360));
        using var nameBrush = new SolidBrush(Color.FromArgb(235, 42, 48, 56));
        using var nameBack = new SolidBrush(Color.FromArgb(230, 248, 249, 252));
        using var nameBorder = new Pen(Color.FromArgb(150, 122, 134, 152));
        var lines = WrapTextureProbeName(g, textureName, maxWidth - 16);
        var lineHeight = Font.GetHeight(g) + 2f;
        var textWidth = lines.Count == 0 ? 0f : lines.Max(line => g.MeasureString(line, Font).Width);
        var rectWidth = Math.Min(maxWidth, MathF.Ceiling(textWidth + 16f));
        var rectHeight = MathF.Ceiling(lines.Count * lineHeight + 8f);
        var left = circle.Left + (circle.Width - rectWidth) * 0.5f;
        left = Math.Clamp(left, 8f, Math.Max(8f, ClientSize.Width - rectWidth - 8f));
        var top = circle.Bottom + 8f;
        if (top + rectHeight > ClientSize.Height - 10)
        {
            top = circle.Top - rectHeight - 8f;
        }

        var rect = new RectangleF(left, top, rectWidth, rectHeight);
        using var path = RoundedRect(rect, 3f);
        g.FillPath(nameBack, path);
        g.DrawPath(nameBorder, path);

        for (var i = 0; i < lines.Count; i++)
        {
            g.DrawString(lines[i], Font, nameBrush, rect.Left + 8f, rect.Top + 4f + i * lineHeight);
        }
    }

    private List<string> WrapTextureProbeName(Graphics g, string textureName, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(textureName))
        {
            return [""];
        }

        var start = 0;
        while (start < textureName.Length)
        {
            var best = 1;
            for (var length = 1; start + length <= textureName.Length; length++)
            {
                var candidate = textureName.Substring(start, length);
                if (g.MeasureString(candidate, Font).Width > maxWidth)
                {
                    break;
                }

                best = length;
            }

            lines.Add(textureName.Substring(start, best));
            start += best;
        }

        return lines;
    }

    private List<TextureProbeLayer> BuildTextureProbeLayers(TextureProbeHit hit)
    {
        var layers = new List<TextureProbeLayer>();
        if (_mesh is null || hit.SubmeshIndex < 0 || hit.SubmeshIndex >= _mesh.Submeshes.Count)
        {
            return layers;
        }

        var submesh = _mesh.Submeshes[hit.SubmeshIndex];
        _textures.TryGetValue(hit.SubmeshIndex, out var textures);
        AddTextureProbeLayer(layers, "diffuse", "Diffuse", TextureProbeLayerKind.Color, textures?.Diffuse, hit.U, hit.V, submesh);
        AddTextureProbeLayer(layers, "bump", "Normal Map", TextureProbeLayerKind.Normal, textures?.Normal, hit.U, hit.V, submesh);
        AddTextureProbeLayer(layers, "detail_diffuse", "Detail", TextureProbeLayerKind.Detail, textures?.Detail, hit.DetailU, hit.DetailV, submesh);
        AddTextureProbeLayer(layers, "bake", "Lighting Map", TextureProbeLayerKind.Color, textures?.Bake, hit.BakeU, hit.BakeV, submesh);
        AddTextureProbeLayer(layers, "shadow", "Shadow", TextureProbeLayerKind.Color, textures?.Shadow, hit.ShadowU, hit.ShadowV, submesh);
        AddTextureProbeLayer(layers, "occlusion", "Ambient Occlusion", TextureProbeLayerKind.Auxiliary, textures?.Occlusion, hit.U, hit.V, submesh);
        AddAuxiliaryTextureProbeLayers(layers, textures, hit, submesh);
        return layers;
    }

    private static void AddAuxiliaryTextureProbeLayers(
        List<TextureProbeLayer> layers,
        MaterialTextureSet? textures,
        TextureProbeHit hit,
        SubmeshData submesh)
    {
        if (textures is null)
        {
            return;
        }

        foreach (var (slot, texture) in textures.Auxiliary.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddTextureProbeLayer(
                layers,
                slot,
                ResolveAuxiliaryTextureProbeLabel(slot, texture),
                TextureProbeLayerKind.Auxiliary,
                texture,
                hit.U,
                hit.V,
                submesh);
        }
    }

    private static void AddTextureProbeLayer(
        List<TextureProbeLayer> layers,
        string slot,
        string fallbackLabel,
        TextureProbeLayerKind kind,
        TextureImage? texture,
        float u,
        float v,
        SubmeshData submesh)
    {
        if (texture is null)
        {
            return;
        }

        var label = ResolveTextureProbeLabel(slot, fallbackLabel, submesh, texture);
        if (layers.Any(layer => ReferenceEquals(layer.Texture, texture) && layer.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        layers.Add(new TextureProbeLayer(label, GetTextureProbeName(slot, submesh, texture), kind, texture, u, v));
    }

    private string? GetActiveTextureProbeLayerLabel()
    {
        var hit = _lockedTextureProbeHit;
        if (hit is null && TryGetTextureProbeHit(_textureProbeMouse, out var hoverHit))
        {
            hit = hoverHit;
        }

        if (hit is not TextureProbeHit activeHit)
        {
            return null;
        }

        var layers = BuildTextureProbeLayers(activeHit);
        return _textureProbeLayerIndex >= 0 && _textureProbeLayerIndex < layers.Count
            ? layers[_textureProbeLayerIndex].Label
            : null;
    }

    private void RestoreTextureProbeLayerIndex(TextureProbeHit hit, string? preferredLabel)
    {
        var layers = BuildTextureProbeLayers(hit);
        if (layers.Count == 0)
        {
            _textureProbeLayerIndex = 0;
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredLabel))
        {
            for (var i = 0; i < layers.Count; i++)
            {
                if (layers[i].Label.Equals(preferredLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _textureProbeLayerIndex = i;
                    return;
                }
            }
        }

        _textureProbeLayerIndex = Math.Clamp(_textureProbeLayerIndex, 0, layers.Count - 1);
    }

    private static string ResolveTextureProbeLabel(string slot, string fallbackLabel, SubmeshData submesh, TextureImage texture)
    {
        var textureName = "";
        if (!submesh.TextureNames.TryGetValue(slot, out textureName))
        {
            textureName = Path.GetFileNameWithoutExtension(texture.SourcePath);
        }

        if (slot.Equals("detail_diffuse", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizeTextureName(textureName);
            if (normalized.Contains("ink", StringComparison.OrdinalIgnoreCase))
            {
                return "InkLines";
            }

            if (normalized.Contains("line", StringComparison.OrdinalIgnoreCase))
            {
                return "Lines";
            }
        }

        return fallbackLabel;
    }

    private static string ResolveAuxiliaryTextureProbeLabel(string slot, TextureImage texture)
    {
        var name = NormalizeTextureName(Path.GetFileNameWithoutExtension(texture.SourcePath));
        var key = NormalizeTextureName(slot) + "|" + name;
        if (key.Contains("light", StringComparison.OrdinalIgnoreCase))
        {
            return "Lighting Map";
        }

        if (key.Contains("_ao", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("ao", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("occlusion", StringComparison.OrdinalIgnoreCase))
        {
            return "Ambient Occlusion";
        }

        if (key.Contains("spec", StringComparison.OrdinalIgnoreCase))
        {
            return "Specular";
        }

        if (key.Contains("mask", StringComparison.OrdinalIgnoreCase))
        {
            return "Mask";
        }

        if (key.Contains("env", StringComparison.OrdinalIgnoreCase))
        {
            return "Environment";
        }

        if (key.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("glow", StringComparison.OrdinalIgnoreCase))
        {
            return "Emissive";
        }

        if (key.Contains("gradient", StringComparison.OrdinalIgnoreCase))
        {
            return "Gradient";
        }

        return string.IsNullOrWhiteSpace(slot) ? "Auxiliary Map" : ToTitleLabel(slot);
    }

    private static string ToTitleLabel(string value)
    {
        var parts = NormalizeTextureName(value)
            .Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "Auxiliary Map";
        }

        return string.Join(" ", parts.Select(part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string GetTextureProbeName(string slot, SubmeshData submesh, TextureImage texture)
    {
        if (submesh.TextureNames.TryGetValue(slot, out var textureName) && !string.IsNullOrWhiteSpace(textureName))
        {
            return textureName;
        }

        var sourceName = Path.GetFileNameWithoutExtension(texture.SourcePath);
        return string.IsNullOrWhiteSpace(sourceName) ? slot : sourceName;
    }

    private static bool UsesTextureAlpha(PreviewRenderMode renderMode)
        => renderMode is PreviewRenderMode.Shaded or PreviewRenderMode.Unlit;

    private static (float R, float G, float B, float A) BuildDebugVertexColor(
        VertexData vertex,
        Vector3 normal,
        SubmeshData submesh,
        int submeshIndex,
        int[]? boneMap,
        PreviewRenderMode renderMode)
    {
        return renderMode switch
        {
            PreviewRenderMode.TextureSlotDebug => BuildTextureSlotDebugColor(submesh, submeshIndex),
            PreviewRenderMode.Normals => BuildNormalDebugColor(normal),
            PreviewRenderMode.VertexColor => BuildVertexColorDebugColor(vertex),
            PreviewRenderMode.SkinWeights => BuildSkinWeightDebugColor(vertex, boneMap),
            _ => (1f, 1f, 1f, 1f),
        };
    }

    private static int BuildUvDebugColor(float u, float v)
    {
        var fu = Fract(u);
        var fv = Fract(v);
        var checker = (((int)MathF.Floor(fu * 10f) + (int)MathF.Floor(fv * 10f)) & 1) == 0 ? 1f : 0.62f;
        var gridU = Math.Min(fu, 1f - fu) < 0.015f;
        var gridV = Math.Min(fv, 1f - fv) < 0.015f;
        if (gridU || gridV)
        {
            return Color.FromArgb(255, 24, 26, 30).ToArgb();
        }

        return Color.FromArgb(
            255,
            Math.Clamp((int)MathF.Round((32f + fu * 210f) * checker), 0, 255),
            Math.Clamp((int)MathF.Round((42f + fv * 200f) * checker), 0, 255),
            Math.Clamp((int)MathF.Round((210f - fu * 90f + fv * 35f) * checker), 0, 255)).ToArgb();
    }

    private static float Fract(float value)
    {
        var floor = MathF.Floor(value);
        return value - floor;
    }

    private static (float R, float G, float B, float A) BuildTextureSlotDebugColor(SubmeshData submesh, int submeshIndex)
    {
        if (submesh.TextureNames.Count == 0)
        {
            return HashToColor($"submesh:{submeshIndex}:{submesh.Name}", 0.72f, 0.95f);
        }

        var r = 0f;
        var g = 0f;
        var b = 0f;
        var count = 0;
        foreach (var (slot, textureName) in submesh.TextureNames.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var slotColor = SlotCategoryColor(slot);
            var nameColor = HashToColor(textureName, 0.65f, 0.98f);
            r += slotColor.R * 0.68f + nameColor.R * 0.32f;
            g += slotColor.G * 0.68f + nameColor.G * 0.32f;
            b += slotColor.B * 0.68f + nameColor.B * 0.32f;
            count++;
        }

        return (
            Math.Clamp(r / count, 0f, 1f),
            Math.Clamp(g / count, 0f, 1f),
            Math.Clamp(b / count, 0f, 1f),
            1f);
    }

    private static (float R, float G, float B, float A) SlotCategoryColor(string slot)
    {
        var normalized = NormalizeTextureName(slot).ToLowerInvariant();
        if (normalized.Contains("diffuse") || normalized.Contains("albedo") || normalized.Contains("base"))
        {
            return (0.20f, 0.72f, 0.38f, 1f);
        }

        if (normalized.Contains("bump") || normalized.Contains("normal"))
        {
            return (0.32f, 0.42f, 0.96f, 1f);
        }

        if (normalized.Contains("detail") || normalized.Contains("line") || normalized.Contains("ink"))
        {
            return (0.98f, 0.55f, 0.16f, 1f);
        }

        if (normalized.Contains("bake") || normalized.Contains("light"))
        {
            return (0.96f, 0.84f, 0.25f, 1f);
        }

        if (normalized.Contains("shadow"))
        {
            return (0.45f, 0.30f, 0.78f, 1f);
        }

        if (normalized.Contains("occlusion") || normalized.EndsWith("ao", StringComparison.Ordinal))
        {
            return (0.48f, 0.54f, 0.58f, 1f);
        }

        return HashToColor(normalized, 0.62f, 0.92f);
    }

    private static (float R, float G, float B, float A) BuildNormalDebugColor(Vector3 normal)
    {
        if (normal.LengthSquared() < 0.000001f)
        {
            normal = Vector3.UnitZ;
        }
        else
        {
            normal = Vector3.Normalize(normal);
        }

        return (
            Math.Clamp(normal.X * 0.5f + 0.5f, 0f, 1f),
            Math.Clamp(normal.Y * 0.5f + 0.5f, 0f, 1f),
            Math.Clamp(normal.Z * 0.5f + 0.5f, 0f, 1f),
            1f);
    }

    private static (float R, float G, float B, float A) BuildVertexColorDebugColor(VertexData vertex)
    {
        var r = Math.Clamp(vertex.ColorR, 0f, 1f);
        var g = Math.Clamp(vertex.ColorG, 0f, 1f);
        var b = Math.Clamp(vertex.ColorB, 0f, 1f);
        var a = Math.Clamp(vertex.ColorA, 0f, 1f);
        if (r + g + b <= 0.001f)
        {
            return (0.02f, 0.02f, 0.02f, 1f);
        }

        return (r, g, b, Math.Clamp(0.35f + a * 0.65f, 0f, 1f));
    }

    private static (float R, float G, float B, float A) BuildSkinWeightDebugColor(VertexData vertex, int[]? boneMap)
    {
        var bones = new[] { vertex.Bone0, vertex.Bone1, vertex.Bone2, vertex.Bone3 };
        var weights = new[] { vertex.Weight0, vertex.Weight1, vertex.Weight2, vertex.Weight3 };
        var bestIndex = 0;
        var bestWeight = 0f;
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] > bestWeight)
            {
                bestWeight = weights[i];
                bestIndex = i;
            }
        }

        if (bestWeight <= 0.0001f)
        {
            return (0.18f, 0.18f, 0.18f, 1f);
        }

        var bone = bones[bestIndex];
        if (boneMap is not null && (uint)bone < boneMap.Length && boneMap[bone] >= 0)
        {
            bone = boneMap[bone];
        }

        var color = HashToColor($"bone:{bone}", 0.78f, 1f);
        var intensity = Math.Clamp(0.28f + bestWeight * 0.72f, 0f, 1f);
        return (color.R * intensity, color.G * intensity, color.B * intensity, 1f);
    }

    private static (float R, float G, float B, float A) HashToColor(string value, float saturation, float brightness)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            var hue = (hash % 360) / 360f;
            return HsvToRgb(hue, saturation, brightness);
        }
    }

    private static (float R, float G, float B, float A) HsvToRgb(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1f - MathF.Abs((h * 6f) % 2f - 1f));
        var m = v - c;
        var sector = (int)MathF.Floor(h * 6f);
        var (r, g, b) = sector switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };

        return (r + m, g + m, b + m, 1f);
    }

    private static string ShortenMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var keep = Math.Max(4, (maxLength - 3) / 2);
        return value[..keep] + "..." + value[^keep..];
    }

    private static int WrapIndex(int value, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var wrapped = value % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    private static int VisualizeNormalProbePixel(int argb, bool swizzled)
    {
        var nxByte = swizzled ? (argb >> 24) & 0xFF : (argb >> 16) & 0xFF;
        var nyByte = swizzled ? argb & 0xFF : (argb >> 8) & 0xFF;
        var nx = nxByte / 127.5f - 1f;
        var ny = -(nyByte / 127.5f - 1f);
        var nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
        return Color.FromArgb(255, ToNormalByte(nx), ToNormalByte(ny), ToNormalByte(nz)).ToArgb();
    }

    private static int VisualizeDetailProbePixel(int argb, TextureImage texture)
    {
        var alpha = ((argb >> 24) & 0xFF) / 255f;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        var luminance = (r * 0.30f + g * 0.59f + b * 0.11f) / 255f;
        var coverage = texture.AverageAlpha < 0.5f ? alpha : 1f - alpha;

        if (coverage < 0.025f && luminance < 0.06f)
        {
            return Color.FromArgb(255, 245, 247, 250).ToArgb();
        }

        if (coverage > 0.025f)
        {
            var value = Math.Clamp((int)MathF.Round(245f - coverage * 220f), 18, 245);
            return Color.FromArgb(255, value, value, value).ToArgb();
        }

        var gray = Math.Clamp((int)MathF.Round(luminance * 255f), 0, 255);
        return Color.FromArgb(255, gray, gray, gray).ToArgb();
    }

    private static int VisualizeAuxiliaryProbePixel(int argb, TextureImage texture)
    {
        if (!IsMostlyGrayscale(texture))
        {
            return argb | unchecked((int)0xFF000000);
        }

        var alpha = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        var gray = Math.Clamp((int)MathF.Round(r * 0.30f + g * 0.59f + b * 0.11f), 0, 255);
        if (texture.AverageAlpha < 0.99f)
        {
            gray = Math.Clamp((gray + alpha) / 2, 0, 255);
        }

        return Color.FromArgb(255, gray, gray, gray).ToArgb();
    }

    private static bool IsMostlyGrayscale(TextureImage texture)
    {
        if (texture.Pixels.Length == 0)
        {
            return false;
        }

        var total = texture.Pixels.Length;
        var step = Math.Max(1, total / Math.Min(4096, total));
        var samples = 0;
        var grayish = 0;
        for (var i = 0; i < total; i += step)
        {
            var argb = texture.Pixels[i];
            var r = (argb >> 16) & 0xFF;
            var g = (argb >> 8) & 0xFF;
            var b = argb & 0xFF;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max - min <= 24 || (r + g + b < 54))
            {
                grayish++;
            }

            samples++;
        }

        return grayish >= samples * 0.72f;
    }

    private static bool IsLikelyTelltaleSwizzledNormal(TextureImage texture)
    {
        var total = texture.Pixels.Length;
        if (total == 0)
        {
            return false;
        }

        var step = Math.Max(1, total / Math.Min(10000, total));
        var samples = 0;
        var sumR = 0L;
        var sumG = 0L;
        var sumB = 0L;
        var sumA = 0L;
        for (var i = 0; i < total; i += step)
        {
            var pixel = texture.Pixels[i];
            sumA += (pixel >> 24) & 0xFF;
            sumR += (pixel >> 16) & 0xFF;
            sumG += (pixel >> 8) & 0xFF;
            sumB += pixel & 0xFF;
            samples++;
        }

        var avgR = sumR / (float)samples;
        var avgG = sumG / (float)samples;
        var avgB = sumB / (float)samples;
        var avgA = sumA / (float)samples;
        return avgR > 240f &&
               avgG > 220f &&
               avgB is > 70f and < 185f &&
               avgA is > 70f and < 185f;
    }

    private static int ToNormalByte(float value)
        => Math.Clamp((int)MathF.Round((value * 0.5f + 0.5f) * 255f), 0, 255);

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180f, 90f);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270f, 90f);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private void DrawSkeleton(Graphics g, Matrix4x4 transform, float scale, PointF center)
    {
        var worldMatrices = BuildBoneWorldMatrices(_skeleton!, _boneOffsets, _boneRotations);
        var world = ExtractBoneWorldPositions(worldMatrices);
        var visibleBones = BuildVisibleSkeletonBones();
        using var bonePen = new Pen(Color.FromArgb(240, 255, 196, 72), 2f);
        using var jointBrush = new SolidBrush(Color.FromArgb(245, 255, 235, 145));
        using var selectedPen = new Pen(Color.FromArgb(255, 80, 225, 255), 3f);
        using var selectedBrush = new SolidBrush(Color.FromArgb(255, 80, 225, 255));

        for (var i = 0; i < _skeleton!.Bones.Count; i++)
        {
            if (visibleBones is not null && !visibleBones.Contains(i))
            {
                continue;
            }

            var parent = _skeleton.Bones[i].ParentIndex;
            var p = Project(world[i], transform, scale, center);
            var isSelected = i == _selectedBone;
            g.FillEllipse(isSelected ? selectedBrush : jointBrush, p.X - 2.5f, p.Y - 2.5f, 5f, 5f);
            if (parent >= 0 &&
                parent < world.Length &&
                (visibleBones is null || visibleBones.Contains(parent)))
            {
                var pp = Project(world[parent], transform, scale, center);
                g.DrawLine(isSelected ? selectedPen : bonePen, pp, p);
            }

            if (!HasVisibleChild(i, visibleBones) &&
                TryGetRichTerminalBoneEnd(_skeleton.Bones[i], worldMatrices[i], out var terminalEnd))
            {
                var ep = Project(terminalEnd, transform, scale, center);
                g.DrawLine(isSelected ? selectedPen : bonePen, p, ep);
            }
        }
    }

    private static Vector3[] ExtractBoneWorldPositions(IReadOnlyList<Matrix4x4> worldMatrices)
    {
        var result = new Vector3[worldMatrices.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Vector3.Transform(Vector3.Zero, worldMatrices[i]);
        }

        return result;
    }

    private bool HasVisibleChild(int boneIndex, IReadOnlySet<int>? visibleBones)
    {
        if (_skeleton is null)
        {
            return false;
        }

        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            if (_skeleton.Bones[i].ParentIndex == boneIndex &&
                (visibleBones is null || visibleBones.Contains(i)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetRichTerminalBoneEnd(BoneData bone, Matrix4x4 worldMatrix, out Vector3 end)
    {
        end = default;
        if (bone.BoneLength <= 0.000001f || bone.BoneDir.LengthSquared() <= 0.000001f)
        {
            return false;
        }

        var start = Vector3.Transform(Vector3.Zero, worldMatrix);
        var direction = Vector3.TransformNormal(Vector3.Normalize(bone.BoneDir) * bone.BoneLength, worldMatrix);
        if (direction.LengthSquared() <= 0.000001f)
        {
            return false;
        }

        end = start + direction;
        return true;
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
        var state = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildBoneWorldMatrix(skeleton, i, offsets, rotations, worldMatrices, state);
        }

        return worldMatrices;
    }

    private static Matrix4x4 BuildBoneWorldMatrix(
        SkeletonData skeleton,
        int index,
        IReadOnlyDictionary<int, Vector3>? offsets,
        IReadOnlyDictionary<int, Quaternion>? rotations,
        Matrix4x4[] worldMatrices,
        byte[] state)
    {
        if (state[index] == 2)
        {
            return worldMatrices[index];
        }

        if (state[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        state[index] = 1;
        var bone = skeleton.Bones[index];
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
        offsets?.TryGetValue(index, out offset);
        if (rotations is not null && rotations.TryGetValue(index, out var extraRotation))
        {
            rotation = Quaternion.Normalize(extraRotation * rotation);
        }

        var local =
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(bone.X + offset.X, bone.Y + offset.Y, bone.Z + offset.Z);

        var parent = bone.ParentIndex;
        worldMatrices[index] = parent >= 0 && parent < skeleton.Bones.Count
            ? local * BuildBoneWorldMatrix(skeleton, parent, offsets, rotations, worldMatrices, state)
            : local;
        state[index] = 2;
        return worldMatrices[index];
    }

    private int[]? BuildBoneMap(SubmeshData submesh)
    {
        if (_mesh is null || _skeleton is null)
        {
            return null;
        }

        if (_mesh.BonePalettes.Count == 0 && _mesh.Version == 1)
        {
            if (!HasExplicitSkinningData(submesh))
            {
                return null;
            }

            var directMap = new int[_skeleton.Bones.Count];
            for (var i = 0; i < directMap.Length; i++)
            {
                directMap[i] = i;
            }

            return directMap;
        }

        if (_mesh.BonePalettes.Count == 0)
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

    private static bool HasExplicitSkinningData(SubmeshData submesh)
    {
        foreach (var vertex in submesh.Vertices)
        {
            if (vertex.Bone0 != 0 ||
                vertex.Bone1 != 0 ||
                vertex.Bone2 != 0 ||
                vertex.Bone3 != 0 ||
                MathF.Abs(vertex.Weight0 - 1f) > 0.000001f ||
                MathF.Abs(vertex.Weight1) > 0.000001f ||
                MathF.Abs(vertex.Weight2) > 0.000001f ||
                MathF.Abs(vertex.Weight3) > 0.000001f)
            {
                return true;
            }
        }

        return false;
    }

    private Vector3 ApplySkinning(VertexData vertex, int[]? boneMap, Matrix4x4[]? baseMatrices, Matrix4x4[]? posedMatrices)
    {
        var original = ToVector(vertex);
        if (boneMap is null || baseMatrices is null || posedMatrices is null || ReferenceEquals(baseMatrices, posedMatrices))
        {
            return original;
        }

        var version = _mesh?.Version ?? 0;
        var result = Vector3.Zero;
        var total = 0f;
        AccumulateSkinned(vertex.Bone0, vertex.Weight0, version, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone1, vertex.Weight1, version, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone2, vertex.Weight2, version, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);
        AccumulateSkinned(vertex.Bone3, vertex.Weight3, version, original, boneMap, baseMatrices, posedMatrices, ref result, ref total);

        if (total <= 0.000001f)
        {
            return original;
        }

        return result / total;
    }

    private Vector3 ApplyPose(
        VertexData vertex,
        Matrix4x4? rigidPose,
        int[]? boneMap,
        Matrix4x4[]? baseMatrices,
        Matrix4x4[]? posedMatrices)
    {
        var original = ToVector(vertex);
        if (rigidPose is Matrix4x4 rigid)
        {
            return Vector3.Transform(original, rigid);
        }

        return ApplySkinning(vertex, boneMap, baseMatrices, posedMatrices);
    }

    private static Vector3 ApplyPoseNormal(VertexData vertex, Matrix4x4? rigidPose)
    {
        var normal = ToNormal(vertex);
        if (rigidPose is not Matrix4x4 rigid)
        {
            return normal;
        }

        rigid.M41 = 0f;
        rigid.M42 = 0f;
        rigid.M43 = 0f;
        return Vector3.TransformNormal(normal, rigid);
    }

    private Matrix4x4? BuildRigidPoseMatrix(
        SubmeshData submesh,
        int[]? boneMap,
        Matrix4x4[]? baseMatrices,
        Matrix4x4[]? posedMatrices)
    {
        if (_mesh is null ||
            _skeleton is null ||
            _mesh.Version != 1 ||
            submesh.RigidBoneIndex < 0 ||
            boneMap is not null ||
            baseMatrices is null ||
            posedMatrices is null ||
            ReferenceEquals(baseMatrices, posedMatrices))
        {
            return null;
        }

        var skeletonBone = ResolveRigidSkeletonBone(submesh.RigidBoneIndex, baseMatrices);
        if (skeletonBone < 0 || skeletonBone >= baseMatrices.Length || skeletonBone >= posedMatrices.Length)
        {
            return null;
        }

        return Matrix4x4.Invert(baseMatrices[skeletonBone], out var inverseBase)
            ? inverseBase * posedMatrices[skeletonBone]
            : null;
    }

    private int ResolveRigidSkeletonBone(int rigidBoneIndex, Matrix4x4[] baseMatrices)
    {
        if (_mesh is null || _skeleton is null || rigidBoneIndex < 0)
        {
            return -1;
        }

        _rigidBoneMap ??= BuildRigidBoneMap(baseMatrices);
        return _rigidBoneMap.TryGetValue(rigidBoneIndex, out var skeletonBone) ? skeletonBone : -1;
    }

    private Dictionary<int, int> BuildRigidBoneMap(Matrix4x4[] baseMatrices)
    {
        var map = new Dictionary<int, int>();
        if (_mesh is null || _skeleton is null)
        {
            return map;
        }

        foreach (var group in _mesh.Submeshes
                     .Where(submesh => submesh.RigidBoneIndex >= 0 && submesh.Vertices.Count > 0)
                     .GroupBy(submesh => submesh.RigidBoneIndex))
        {
            if (_mesh.Version == 1 && group.Key == 0 && TryFindVehicleBodyBone(out var bodyBone))
            {
                map[group.Key] = bodyBone;
                continue;
            }

            map[group.Key] = FindNearestSkeletonBone(GetSubmeshGroupCenter(group), baseMatrices, skipRoot: group.Key > 0);
        }

        return map;
    }

    private int FindNearestSkeletonBone(Vector3 center, Matrix4x4[] baseMatrices, bool skipRoot)
    {
        if (_skeleton is null)
        {
            return -1;
        }

        var bestIndex = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < _skeleton.Bones.Count && i < baseMatrices.Length; i++)
        {
            if (skipRoot && IsBttfStaticRootBone(i))
            {
                continue;
            }

            var bonePosition = Vector3.Transform(Vector3.Zero, baseMatrices[i]);
            var distance = Vector3.DistanceSquared(center, bonePosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private bool IsBttfStaticRootBone(int boneIndex)
    {
        if (_mesh?.Version != 1 || _skeleton is null || boneIndex < 0 || boneIndex >= _skeleton.Bones.Count)
        {
            return false;
        }

        if (boneIndex == 0)
        {
            return true;
        }

        return _skeleton.Bones[boneIndex].Name.Equals("root", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryFindVehicleBodyBone(out int bodyBone)
    {
        bodyBone = -1;
        if (_skeleton is null || !HasVehicleWheelBones())
        {
            return false;
        }

        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            if (_skeleton.Bones[i].Name.Equals("body", StringComparison.OrdinalIgnoreCase))
            {
                bodyBone = i;
                return true;
            }
        }

        bodyBone = 0;
        return _skeleton.Bones.Count > 0;
    }

    private bool HasVehicleWheelBones()
    {
        if (_skeleton is null)
        {
            return false;
        }

        var wheelCount = 0;
        foreach (var bone in _skeleton.Bones)
        {
            if (bone.Name.Contains("wheel", StringComparison.OrdinalIgnoreCase))
            {
                wheelCount++;
            }
        }

        return wheelCount >= 2;
    }

    private static Vector3 GetSubmeshCenter(SubmeshData submesh)
        => GetSubmeshGroupCenter([submesh]);

    private static Vector3 GetSubmeshGroupCenter(IEnumerable<SubmeshData> submeshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var submesh in submeshes)
        {
            foreach (var vertex in submesh.Vertices)
            {
                var p = ToVector(vertex);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        return any ? (min + max) * 0.5f : Vector3.Zero;
    }

    private static void AccumulateSkinned(
        int paletteBone,
        float weight,
        int meshVersion,
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

        var paletteIndex = NormalizePaletteBoneIndex(paletteBone, boneMap.Length, meshVersion);
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

    private static int NormalizePaletteBoneIndex(int rawIndex, int paletteLength, int meshVersion)
    {
        if (rawIndex < 0)
        {
            return -1;
        }

        // Use the same per-version convention as the parser and the glTF export. v17/v18 store the direct
        // palette index; older versions store the index times 3. Re-dividing a v18 direct index (the old
        // behaviour) sent every index that was a multiple of 3 to the wrong bone — e.g. the right ankle
        // resolved to a left-leg bone, which is why posing one limb moved another in the combined preview.
        var index = BoneIndexConvention.ToPaletteIndex(rawIndex, meshVersion);
        return index >= 0 && index < paletteLength ? index : -1;
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
        var visibleBones = BuildVisibleSkeletonBones();
        var influencedBones = BuildInfluencedSkeletonBones();
        var bestIndex = -1;
        var bestDistance = 14f;
        var bestIsInfluenced = false;
        for (var i = 0; i < world.Length; i++)
        {
            if (visibleBones is not null && !visibleBones.Contains(i))
            {
                continue;
            }

            var p = Project(world[i], transform, scale, center);
            var distance = MathF.Sqrt((p.X - location.X) * (p.X - location.X) + (p.Y - location.Y) * (p.Y - location.Y));
            var isInfluenced = influencedBones.Contains(i);
            if (IsBetterBonePick(distance, isInfluenced, bestDistance, bestIsInfluenced))
            {
                bestDistance = distance;
                bestIndex = i;
                bestIsInfluenced = isInfluenced;
            }
        }

        for (var i = 0; i < _skeleton.Bones.Count; i++)
        {
            if (visibleBones is not null && !visibleBones.Contains(i))
            {
                continue;
            }

            var parent = _skeleton.Bones[i].ParentIndex;
            if (parent < 0 ||
                parent >= world.Length ||
                (visibleBones is not null && !visibleBones.Contains(parent)))
            {
                continue;
            }

            var a = Project(world[parent], transform, scale, center);
            var b = Project(world[i], transform, scale, center);
            var distance = DistanceToSegment(location, a, b);
            var isInfluenced = influencedBones.Contains(i);
            if (IsBetterBonePick(distance, isInfluenced, bestDistance, bestIsInfluenced))
            {
                bestDistance = distance;
                bestIndex = i;
                bestIsInfluenced = isInfluenced;
            }
        }

        return FindPoseBoneWithInfluence(bestIndex);
    }

    private static bool IsBetterBonePick(float distance, bool isInfluenced, float bestDistance, bool bestIsInfluenced)
    {
        const float tieEpsilon = 0.75f;
        if (distance < bestDistance - tieEpsilon)
        {
            return true;
        }

        return isInfluenced && !bestIsInfluenced && distance <= bestDistance + tieEpsilon;
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
        if (parent >= 0 && parent < _skeleton.Bones.Count)
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
        while (current >= 0)
        {
            if (influenced.Contains(current) || HasInfluencedDescendant(current, influenced))
            {
                return current;
            }

            current = _skeleton.Bones[current].ParentIndex;
        }

        return boneIndex;
    }

    private bool HasInfluencedDescendant(int boneIndex, IReadOnlySet<int> influenced)
    {
        if (_skeleton is null)
        {
            return false;
        }

        foreach (var influencedBone in influenced)
        {
            var current = influencedBone;
            var guard = 0;
            while (current >= 0 && current < _skeleton.Bones.Count && guard++ < _skeleton.Bones.Count)
            {
                current = _skeleton.Bones[current].ParentIndex;
                if (current == boneIndex)
                {
                    return true;
                }
            }
        }

        return false;
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

            var version = _mesh.Version;
            foreach (var vertex in submesh.Vertices)
            {
                AddInfluencedBone(vertex.Bone0, vertex.Weight0, version, map, influenced);
                AddInfluencedBone(vertex.Bone1, vertex.Weight1, version, map, influenced);
                AddInfluencedBone(vertex.Bone2, vertex.Weight2, version, map, influenced);
                AddInfluencedBone(vertex.Bone3, vertex.Weight3, version, map, influenced);
            }
        }

        return influenced;
    }

    private HashSet<int>? BuildVisibleSkeletonBones()
    {
        if (_mesh is null || _skeleton is null || _skeleton.Bones.Count == 0)
        {
            return null;
        }

        var visible = BuildInfluencedSkeletonBones();
        if (visible.Count == 0 && _mesh.BonePalettes.Count > 0)
        {
            foreach (var submesh in _mesh.Submeshes)
            {
                var map = BuildBoneMap(submesh);
                if (map is null)
                {
                    continue;
                }

                foreach (var skeletonIndex in map)
                {
                    if (skeletonIndex >= 0)
                    {
                        visible.Add(skeletonIndex);
                    }
                }
            }
        }

        AddVisibleAncestors(visible);

        return visible.Count > 0 && visible.Count < _skeleton.Bones.Count
            ? visible
            : null;
    }

    private void AddVisibleAncestors(HashSet<int> visible)
    {
        if (_skeleton is null || visible.Count == 0)
        {
            return;
        }

        foreach (var seed in visible.ToArray())
        {
            var current = seed;
            var guard = 0;
            while (current >= 0 && current < _skeleton.Bones.Count && guard++ < _skeleton.Bones.Count)
            {
                visible.Add(current);
                current = _skeleton.Bones[current].ParentIndex;
            }
        }
    }

    private static void AddInfluencedBone(int rawBone, float weight, int meshVersion, int[] map, HashSet<int> influenced)
    {
        if (weight <= 0.000001f)
        {
            return;
        }

        var index = NormalizePaletteBoneIndex(rawBone, map.Length, meshVersion);
        if (index >= 0 && index < map.Length && map[index] >= 0)
        {
            influenced.Add(map[index]);
        }
    }

    private Matrix4x4 BuildViewTransform(MeshBounds bounds)
    {
        var viewOrigin = _cameraMode == PreviewCameraMode.Flight
            ? bounds.Center + _flightPosition
            : bounds.Center;
        return Matrix4x4.CreateTranslation(-viewOrigin) *
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
        var darkTheme = BackColor.GetBrightness() < 0.35f;
        using var brush = new LinearGradientBrush(
            ClientRectangle,
            darkTheme ? Color.FromArgb(73, 75, 80) : Color.FromArgb(142, 142, 140),
            darkTheme ? Color.FromArgb(47, 49, 53) : Color.FromArgb(102, 102, 100),
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

    private void DrawEmptyPreview(Graphics g)
    {
        if (!_showDragDropHint || TryGetDragDropImage() is not { } image)
        {
            return;
        }

        var imageRect = GetDragDropRectangle(image);
        var previousInterpolation = g.InterpolationMode;
        var previousPixelOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, imageRect);
        g.InterpolationMode = previousInterpolation;
        g.PixelOffsetMode = previousPixelOffset;
    }

    private void DrawEmptyPreviewBackground(Graphics g)
    {
        if (TryGetEmptyBackgroundImage() is not { } image || Width <= 0 || Height <= 0)
        {
            return;
        }

        var imageRect = GetEmptyBackgroundRectangle(image);

        var previousInterpolation = g.InterpolationMode;
        var previousPixelOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, imageRect);
        g.InterpolationMode = previousInterpolation;
        g.PixelOffsetMode = previousPixelOffset;
    }

    private RectangleF GetEmptyBackgroundRectangle(Image image)
    {
        var maxImageWidth = Width * 0.46f;
        var maxImageHeight = Height * 0.58f;
        var scale = Math.Min(maxImageWidth / image.Width, maxImageHeight / image.Height);
        scale = Math.Min(scale, 1f);
        var imageWidth = image.Width * scale;
        var imageHeight = image.Height * scale;
        var imageX = (Width - imageWidth) * 0.5f;
        var imageY = (Height - imageHeight) * 0.5f;

        return new RectangleF(imageX, imageY, imageWidth, imageHeight);
    }

    private RectangleF GetDragDropRectangle(Image image)
    {
        var maxImageWidth = Math.Min(220f, Width * 0.42f);
        var maxImageHeight = Math.Min(150f, Height * 0.28f);
        var scale = Math.Min(maxImageWidth / image.Width, maxImageHeight / image.Height);
        scale = Math.Min(scale, 1f);
        var imageWidth = image.Width * scale;
        var imageHeight = image.Height * scale;

        var centerX = Width * 0.5f;
        var centerY = Height * 0.5f;
        if (TryGetEmptyBackgroundImage() is { } backgroundImage)
        {
            var backgroundRect = GetEmptyBackgroundRectangle(backgroundImage);
            centerX = backgroundRect.Left + backgroundRect.Width * 0.5f;
            centerY = backgroundRect.Top + backgroundRect.Height * 0.5f;
        }

        return new RectangleF(
            centerX - imageWidth * 0.5f,
            centerY - imageHeight * 0.5f,
            imageWidth,
            imageHeight);
    }

    private Image? TryGetDragDropImage()
    {
        if (_dragDropImage is not null)
        {
            return _dragDropImage;
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TelltaleD3DMeshEditor.Resources.Images.DragDrop.png");
        if (stream is null)
        {
            return null;
        }

        try
        {
            using var loaded = Image.FromStream(stream);
            _dragDropImage = new Bitmap(loaded);
            return _dragDropImage;
        }
        catch
        {
            return null;
        }
    }

    private Image? TryGetEmptyBackgroundImage()
    {
        if (_emptyBackgroundImage is not null)
        {
            return _emptyBackgroundImage;
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TelltaleD3DMeshEditor.Resources.Images.LogoBackground.png");
        if (stream is null)
        {
            return null;
        }

        try
        {
            using var loaded = Image.FromStream(stream);
            _emptyBackgroundImage = new Bitmap(loaded);
            return _emptyBackgroundImage;
        }
        catch
        {
            return null;
        }
    }

    private static Vector3 ToVector(VertexData v) => new(v.X, v.Y, v.Z);
    private static Vector3 ToNormal(VertexData v) => new(v.Nx, v.Ny, v.Nz);

    private static (float U, float V) SelectDetailUv(VertexData vertex)
    {
        // Batman's "_detail" is authored in the SAME UV layout as the diffuse (compare
        // sk61_batman_bodyUpper with sk61_batman_bodyUpper_detail: identical islands). The generic path
        // below prefers UV3, the TWAU-era channel for ink lines, which lands the relief on the wrong
        // part of the body. Michonne already needs the same exception.
        if (GameConfig.Current.Id is GameId.WalkingDeadMichonne or GameId.Batman)
        {
            return (vertex.U, vertex.V);
        }

        if (HasDifferentUv(vertex.U, vertex.V, vertex.U3, vertex.V3))
        {
            return (vertex.U3, vertex.V3);
        }

        return (vertex.U, vertex.V);
    }

    private static (float U, float V) SelectBakeUv(VertexData vertex)
    {
        // Michonne V25 stores the global *_000 lightmap in UV6. UV1 is usually a tiled
        // diffuse channel, so sampling it here creates the repeated horizontal bands.
        if (GameConfig.Current.Id == GameId.WalkingDeadMichonne &&
            HasDifferentUv(vertex.U, vertex.V, vertex.U6, vertex.V6))
        {
            return (vertex.U6, vertex.V6);
        }

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
        return IsHiddenHelperPreviewMaterial(submesh.MaterialName)
            || (submesh.TextureNames.TryGetValue("diffuse", out var diffuse) && IsHiddenHelperPreviewMaterial(diffuse));
    }

    private static bool IsHiddenHelperPreviewMaterial(string? textureName)
    {
        var name = NormalizeTextureName(textureName);
        return string.Equals(name, "color_000", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "map_1px_alpha", StringComparison.OrdinalIgnoreCase);
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
        if (GameConfig.Current.Id == GameId.WalkingDeadMichonne)
        {
            var baseAlpha = (argb >> 24) & 0xFF;
            var rgb = argb & 0x00FFFFFF;
            var alpha = Math.Clamp((int)MathF.Round(baseAlpha * Math.Clamp(tintA, 0f, 1f)), 0, 255);
            return (alpha << 24) | rgb;
        }

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
        var effectiveTintA = GameConfig.Current.Id == GameId.GameOfThrones ? 1f : tintA;
        return Color.FromArgb(
            Math.Clamp((int)(a * effectiveTintA), 0, 255),
            Math.Clamp((int)(r * tintR), 0, 255),
            Math.Clamp((int)(g * tintG), 0, 255),
            Math.Clamp((int)(b * tintB), 0, 255)).ToArgb();
    }

    private static bool HasPreviewVertexAlpha(SubmeshData submesh)
    {
        return GameConfig.Current.Id == GameId.WalkingDeadMichonne &&
               submesh.Vertices.Any(vertex => vertex.ColorA < 0.98f);
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

    // Relief contributed by a two-channel derivative detail map (Batman/GotG "_detail", BC5). Unlike a
    // normal map it is neutral at ZERO, not at 0.5: R/G store how much the surface tilts at that pixel,
    // so seams, folds and panel lines sit where the channels rise. Feeding it through the shade term is
    // what makes that relief visible — those maps carry no colour, so compositing them onto albedo (the
    // old behaviour) could only paint black smudges.
    private static float SampleDetailNormalBoost(TextureImage? detail, float u, float v)
    {
        // Only a genuine two-channel map carries relief. When the two channels are identical the map is
        // a coverage mask, and any directional term cancels itself out (0.6d - 0.8d), which is what made
        // the detail disappear entirely; DetailCompositor multiplies that kind into the albedo instead.
        if (detail is null || !detail.IsTwoChannelDerivativeMap || detail.HasDuplicatedChannels)
        {
            return 1f;
        }

        var argb = detail.Sample(u, v);
        // Neutral for these maps is ZERO, not 128: 93% of Bruce Wayne's head detail sits at exactly 0
        // (flat skin) and only ~7% carries stubble/pores/seams. So the raw channels ARE the tangent-space
        // gradient, read unsigned.
        var dx = ((argb >> 16) & 0xFF) / 255f;
        var dy = ((argb >> 8) & 0xFF) / 255f;
        if (dx + dy < 0.02f)
        {
            return 1f;
        }

        // Light the gradient instead of just darkening by its magnitude. A surface tilted toward the
        // light gets brighter and one tilted away gets darker, which is what makes a bump read as raised
        // rather than as a hole — darkening by magnitude alone inverted every raised seam, and turned the
        // sparse stubble pixels into hard black dots.
        // Light arrives from the upper left in UV space, matching the preview's shading direction.
        const float LightU = -0.6f;
        const float LightV = 0.8f;
        var response = -(dx * LightU + dy * LightV);

        return Math.Clamp(1f + response * 0.45f, 0.82f, 1.18f);
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

    private static int ApplyOcclusion(int baseArgb, TextureImage? occlusion, float u, float v)
    {
        if (occlusion is null)
        {
            return baseArgb;
        }

        var aoArgb = occlusion.Sample(u, v);
        var ao = (((aoArgb >> 16) & 0xFF) * 0.30f +
                  ((aoArgb >> 8) & 0xFF) * 0.59f +
                  (aoArgb & 0xFF) * 0.11f) / 255f;
        var factor = 0.55f + ao * 0.45f;
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
        float ColorA,
        float DebugR,
        float DebugG,
        float DebugB,
        float DebugA);

    private readonly record struct TextureProbeHit(
        int SubmeshIndex,
        float U,
        float V,
        float DetailU,
        float DetailV,
        float BakeU,
        float BakeV,
        float ShadowU,
        float ShadowV)
    {
        public static TextureProbeHit Empty { get; } = new(-1, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
    }

    private enum TextureProbeLayerKind
    {
        Color,
        Normal,
        Detail,
        Auxiliary,
    }

    private sealed record TextureProbeLayer(string Label, string TextureName, TextureProbeLayerKind Kind, TextureImage Texture, float U, float V);
}
