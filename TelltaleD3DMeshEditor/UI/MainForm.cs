using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Core.Localization;
using TelltaleD3DMeshEditor.Formats.Archives;
using TelltaleD3DMeshEditor.Formats.Mesh;
using TelltaleD3DMeshEditor.Formats.Skeleton;
using TelltaleD3DMeshEditor.Formats.Texture;
using TelltaleD3DMeshEditor.Reinsert;
using TelltaleD3DMeshEditor.Viewer;

namespace TelltaleD3DMeshEditor.UI;

// Main Telltale D3DMesh Editor window: asset tree (.d3dmesh + .skl) on the left,
// 3D preview on the right, and toolbar actions for opening folders, extracting
// (GLB or GLTF + files), reimporting edited geometry, and controlling camera/skeleton.
public sealed class MainForm : Form
{
    private const int PreferredTreePanelWidth = 360;
    private const int MinimumTreePanelWidth = 300;
    private const int MaxTreeNodesToMeasureForAutoFit = 1500;
    private const string DiscordInviteUrl = "https://discord.com/invite/HqpnTenqwp";
    private const int DiscordInviteReminderDays = 5;

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStrip _viewerOverlay = new();
    private readonly ToolStripButton _btnOpen = new(Loc.T("toolbar.open_folder"));
    private readonly ToolStripButton _btnOpenArchive = new(Loc.T("toolbar.open_archive"));
    private readonly ToolStripButton _btnExtractSelected = new(Loc.T("toolbar.extract_selected"));
    private readonly ToolStripButton _btnExtractAll = new(Loc.T("toolbar.extract_all"));
    private readonly ToolStripButton _btnExtractAnimations = new(Loc.T("toolbar.extract_animations"));
    private readonly ToolStripButton _btnReimportSelected = new(Loc.T("toolbar.reimport_selected"));
    private readonly ToolStripButton _btnDiffuseAtlas = new(Loc.T("toolbar.texture_atlas"));
    private readonly ToolStripButton _btnCombineParts = new(Loc.T("toolbar.combine_parts"));
    private readonly ToolStripDropDownButton _gameSelector = new();
    private readonly Dictionary<GameId, Image> _gameMenuImages = new();
    private readonly Button _btnFilter = new() { Text = Loc.T("tree.filter") };
    private readonly ContextMenuStrip _filterMenu = new();
    private readonly ToolStripMenuItem _miFilterHasSkeleton = new(Loc.T("filter.has_skeleton"));
    private readonly ToolStripMenuItem _miFilterCharacters = new(Loc.T("filter.characters_only"));
    private readonly Button _btnReload = new() { Text = Loc.T("tree.reload") };
    private readonly ToolStripButton _btnPan = new(Loc.T("viewer.pan"));
    private readonly ToolStripButton _btnPose = new(Loc.T("viewer.pose"));
    private readonly ToolStripDropDownButton _btnView = new(Loc.T("viewer.view"));
    private readonly ToolStripLabel _progressLabel = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripButton _btnCredits = new(Loc.T("toolbar.credits"));
    private readonly ToolStripMenuItem _miFit = new(Loc.T("view.center"));
    private readonly ToolStripMenuItem _miShaded = new(Loc.T("view.shaded"));
    private readonly ToolStripMenuItem _miUnlit = new(Loc.T("view.unlit"));
    private readonly ToolStripMenuItem _miNoTexture = new(Loc.T("view.no_texture"));
    private readonly ToolStripMenuItem _miUvView = new(Loc.T("view.uv_view"));
    private readonly ToolStripMenuItem _miTextureSlotDebug = new(Loc.T("view.texture_slot_debug"));
    private readonly ToolStripMenuItem _miNormals = new(Loc.T("view.normals"));
    private readonly ToolStripMenuItem _miVertexColor = new(Loc.T("view.vertex_color"));
    private readonly ToolStripMenuItem _miSkinWeights = new(Loc.T("view.skin_weights"));
    private readonly ToolStripMenuItem _miPolygons = new(Loc.T("view.polygons"));
    private readonly ToolStripMenuItem _miSkeleton = new(Loc.T("view.skeleton"));
    private readonly ToolStripMenuItem _miAnimations = new(Loc.T("view.animations"));
    private readonly ToolStripMenuItem _miTextureProbe = new(Loc.T("view.texture_probe"));
    private AnimationPlayerPanel? _animationPlayer;
    private readonly ToolStripButton _btnCheckUpdates = new(Loc.T("toolbar.check_updates"));
    private readonly ToolStripButton _btnReportIssue = new(Loc.T("toolbar.report_issue"));
    private readonly ToolStripButton _btnSettings = new(Loc.T("toolbar.settings"));
    private readonly SplitContainer _split = new();
    private readonly TreeView _tree = new();
    private readonly TextBox _searchText = new();
    private readonly System.Windows.Forms.Timer _searchDebounceTimer = new();
    private readonly System.Windows.Forms.Timer _treeFitDebounceTimer = new();
    private readonly MeshPreviewControl _preview = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _detailLabel = new();

    private string? _rootFolder;
    private string? _lastOutputFolder;
    private List<ModelAsset> _assets = [];
    private List<ModelAssetGroup> _assetGroups = [];
    private readonly List<TreeNode> _selectedTreeNodes = [];
    private TreeNode? _selectionAnchorNode;
    private ModelAsset? _selectedAsset;
    private ModelAssetGroup? _selectedGroup;
    private ExportFormat _exportFormat = ExportFormat.Glb;
    private bool _uncompressedTextures;
    private bool _matchOriginalModelSize;
    private bool _normalizeFacialBonesOnReimport;
    private bool _viewerAntiAliasing;
    private bool _viewerFlightCamera;
    private bool _discordRichPresence;
    private int _textureProbeMode;
    private bool _keepViewMenuOpenOnce;
    private bool _filterHasSkeleton;
    private bool _filterCharactersOnly;
    private bool _isBusy;
    private bool _applyingTreeSelection;
    private bool _treeRebuildQueued;
    private readonly string? _initialMeshPath;
    private string? _statusTextBeforeCreditLinkHover;

    public MainForm(string? initialMeshPath = null)
    {
        // Title shows the version and the build tag. Release builds show the build time (in the user's own
        // timezone); Debug builds show "DEBUG" instead, so a development build is never mistaken for a
        // shipped release.
#if DEBUG
        const string build = "DEBUG";
#else
        var build = GetLocalBuildTime().ToString("HH:mm:ss");
#endif
        Text = Loc.T("app.window_title", UpdateChecker.CurrentVersion, build);
        // A bit wider than the English-only minimum so the toolbar fits longer translations (Portuguese /
        // Spanish button labels) without overflowing on first open.
        Width = 1280;
        Height = 760;
        MinimumSize = new Size(920, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        _initialMeshPath = initialMeshPath;

        var preferences = AppPreferences.Load();
        GameConfig.Current = GameConfig.FromId(preferences.LastGame);
        _exportFormat = preferences.OutputFormat.Equals("GltfSeparate", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.GltfSeparate
            : ExportFormat.Glb;
        _btnDiffuseAtlas.Checked = preferences.TextureAtlas;
        _uncompressedTextures = preferences.UncompressedTextures;
        _matchOriginalModelSize = preferences.MatchOriginalModelSize;
        _normalizeFacialBonesOnReimport = preferences.NormalizeFacialBonesOnReimport;
        _viewerAntiAliasing = preferences.ViewerAntiAliasing;
        _viewerFlightCamera = preferences.ViewerFlightCamera;
        _discordRichPresence = preferences.DiscordRichPresence;
        _preview.SetAntiAliasing(_viewerAntiAliasing);
        _preview.SetCameraMode(_viewerFlightCamera ? PreviewCameraMode.Flight : PreviewCameraMode.Orbit);
        _discordPresence.SetEnabled(_discordRichPresence);
        UpdateDiscordPresence();

        BuildUi();
        WireEvents();
        UpdateFormatButton();
        SetReadyState();

        // Quietly check GitHub for a newer release once the window is up. It only notifies when an
        // update exists and never installs anything by itself; failures (offline, etc.) are ignored.
        Shown += async (_, _) =>
        {
            EnsureTreePanelWidth();
            if (!string.IsNullOrWhiteSpace(_initialMeshPath))
            {
                await OpenMeshFileAsync(_initialMeshPath);
            }

            // Elevated relaunch resumes the Open Archive action that hit a protected folder.
            if (PendingArchivePaths is { Length: > 0 } pendingArchives)
            {
                PendingArchivePaths = null;
                await ExtractArchivesAsync(pendingArchives);
            }

            ShowDiscordInviteIfDue();
            await CheckForUpdatesAsync(silent: true);
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _discordPresence.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildUi()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Items.AddRange(new ToolStripItem[]
        {
            _btnOpen,
            _btnOpenArchive,
            new ToolStripSeparator(),
            _btnExtractSelected,
            _btnExtractAll,
            _btnExtractAnimations,
            _btnReimportSelected,
            new ToolStripSeparator(),
            _gameSelector,
            new ToolStripSeparator(),
            _btnCredits,
            _btnSettings,
            _btnReportIssue,
            _btnCheckUpdates
        });

        // Pan / Pose / View live inside the preview as a bottom-right overlay (set up below).
        _viewerOverlay.GripStyle = ToolStripGripStyle.Hidden;
        _viewerOverlay.Dock = DockStyle.None;
        _viewerOverlay.AutoSize = true;
        _viewerOverlay.BackColor = SystemColors.Control;
        // The preview control uses a light-grey ForeColor for its hint text; without overriding it here
        // the overlay's button text would inherit that grey and look disabled.
        _viewerOverlay.ForeColor = SystemColors.ControlText;
        _viewerOverlay.Items.AddRange(new ToolStripItem[]
        {
            _btnCombineParts,
            new ToolStripSeparator(),
            _btnPan,
            _btnPose,
            new ToolStripSeparator(),
            _btnView
        });
        // The overlay sits at the bottom of the window, so the View menu must open upward.
        _btnView.DropDownDirection = ToolStripDropDownDirection.AboveLeft;

        _btnCredits.Alignment = ToolStripItemAlignment.Right;
        _btnSettings.Alignment = ToolStripItemAlignment.Right;
        _btnReportIssue.Alignment = ToolStripItemAlignment.Right;
        _btnCheckUpdates.Alignment = ToolStripItemAlignment.Right;
        // Keep the right-side action buttons pinned to the bar so they are never moved into the overflow
        // dropdown. Longer translations (e.g. "Buscar Actualizaciones", "Verificar Atualizações") otherwise
        // push "Check for Updates" into the hidden overflow area. If the window gets too narrow for the
        // current language, the left-side items overflow instead, keeping these always reachable.
        _btnCredits.Overflow = ToolStripItemOverflow.Never;
        _btnSettings.Overflow = ToolStripItemOverflow.Never;
        _btnReportIssue.Overflow = ToolStripItemOverflow.Never;
        _btnCheckUpdates.Overflow = ToolStripItemOverflow.Never;
        _btnCheckUpdates.ToolTipText = Loc.T("toolbar.check_updates.tooltip");
        _btnReportIssue.ToolTipText = Loc.T("toolbar.report_issue.tooltip");
        _btnSettings.ToolTipText = Loc.T("toolbar.settings.tooltip");
        _btnPan.CheckOnClick = true;
        _btnPose.CheckOnClick = true;
        _btnDiffuseAtlas.CheckOnClick = true;
        _btnCombineParts.CheckOnClick = true;
        _btnOpenArchive.ToolTipText = Loc.T("toolbar.open_archive.tooltip");
        _btnPan.ToolTipText = Loc.T("viewer.pan.tooltip");
        _btnPose.ToolTipText = Loc.T("viewer.pose.tooltip");
        _btnCombineParts.ToolTipText = Loc.T("toolbar.combine_parts.tooltip");
        _btnReimportSelected.ToolTipText = Loc.T("toolbar.reimport_selected.tooltip");
        _btnDiffuseAtlas.ToolTipText = Loc.T("toolbar.texture_atlas.tooltip");
        _btnCredits.ToolTipText = Loc.T("toolbar.credits.tooltip");

        _miShaded.Checked = true;
        _miPolygons.Checked = false;
        _miSkeleton.Checked = false;
        _miTextureProbe.Checked = false;
        _btnView.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miFit,
            new ToolStripSeparator(),
            _miShaded,
            _miUnlit,
            _miNoTexture,
            _miUvView,
            _miTextureSlotDebug,
            _miNormals,
            _miVertexColor,
            _miSkinWeights,
            new ToolStripSeparator(),
            _miPolygons,
            _miSkeleton,
            _miAnimations,
            new ToolStripSeparator(),
            _miTextureProbe
        });
        _btnView.DropDown.ItemClicked += (_, e) =>
        {
            if (ReferenceEquals(e.ClickedItem, _miTextureProbe))
                _keepViewMenuOpenOnce = true;
        };
        _btnView.DropDown.Closing += (_, e) =>
        {
            if (_keepViewMenuOpenOnce && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;

            _keepViewMenuOpenOnce = false;
        };

        _gameSelector.ToolTipText = Loc.T("game_selector.tooltip");
        _gameSelector.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        _gameSelector.TextImageRelation = TextImageRelation.ImageBeforeText;
        _gameSelector.ImageScaling = ToolStripItemImageScaling.None;
        BuildGameSelectorMenu();

        UpdateGameSelector();

        _split.Dock = DockStyle.Fill;
        // Give the splitter a width wide enough to hold both min sizes before applying
        // the panel constraints; otherwise setting Panel2MinSize re-validates SplitterDistance
        // against the default 150px width and throws (Width - Panel2MinSize < Panel1MinSize).
        _split.Size = new Size(PreferredTreePanelWidth + 420 + 200, 700);
        _split.Panel1MinSize = MinimumTreePanelWidth;
        _split.Panel2MinSize = 420;
        _split.SplitterDistance = PreferredTreePanelWidth;
        _split.FixedPanel = FixedPanel.Panel1;
        _split.Panel1.Padding = new Padding(8, 8, 4, 8);
        _split.Panel2.Padding = new Padding(4, 8, 8, 8);
        _split.Panel1.Controls.Add(CreateTreePanel());
        _split.Panel2.Controls.Add(_preview);

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.PathSeparator = Path.DirectorySeparatorChar.ToString();
        _tree.ShowLines = true;
        _tree.ShowPlusMinus = true;
        _tree.ShowNodeToolTips = false;

        _preview.Dock = DockStyle.Fill;

        // Float the Pan/Pose/View controls over the bottom-right corner of the preview.
        _preview.Controls.Add(_viewerOverlay);
        _viewerOverlay.BringToFront();
        _preview.SizeChanged += (_, _) => PositionViewerOverlay();
        _viewerOverlay.SizeChanged += (_, _) => PositionViewerOverlay();

        // Progress lives in the status bar (the conventional spot) so it never overlaps the toolbar.
        _progress.Visible = false;
        _progressLabel.Visible = false;
        _progress.AutoSize = false;
        _progress.Size = new Size(150, 14);
        _progressLabel.AutoSize = false;
        _progressLabel.Width = 140;
        _progressLabel.TextAlign = ContentAlignment.MiddleRight;
        _progressLabel.AutoToolTip = true;
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoToolTip = true;
        _detailLabel.AutoSize = false;
        _detailLabel.Width = 230;
        _detailLabel.TextAlign = ContentAlignment.MiddleRight;
        _detailLabel.AutoToolTip = true;
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(_detailLabel);
        _statusStrip.Items.Add(_progressLabel);
        _statusStrip.Items.Add(_progress);

        Controls.Add(_split);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);
    }

    private Control CreateTreePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // "Filter: SKL" needs more room than the old 70px cell (including its right margin),
        // otherwise WinForms clips the active-filter suffix after the button loses focus.
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _searchText.Dock = DockStyle.Top;
        _searchText.PlaceholderText = Loc.T("tree.search_placeholder");
        _searchText.Margin = new Padding(0, 0, 6, 6);
        _filterMenu.Items.AddRange(new ToolStripItem[]
        {
            _miFilterHasSkeleton,
            _miFilterCharacters
        });
        _btnFilter.Dock = DockStyle.Top;
        _btnFilter.Height = _searchText.PreferredHeight;
        _btnFilter.Margin = new Padding(0, 0, 6, 6);
        _btnReload.Dock = DockStyle.Top;
        _btnReload.Height = _searchText.PreferredHeight;
        _btnReload.Margin = new Padding(0, 0, 0, 6);
        panel.Controls.Add(_searchText, 0, 0);
        panel.Controls.Add(_btnFilter, 1, 0);
        panel.Controls.Add(_btnReload, 2, 0);
        panel.Controls.Add(_tree, 0, 1);
        panel.SetColumnSpan(_tree, 3);
        return panel;
    }

    private void WireEvents()
    {
        _btnOpen.Click += async (_, _) => await OpenFolderDialogAsync();
        _btnOpenArchive.Click += async (_, _) => await OpenArchiveAsync();
        _btnFilter.Click += (_, _) => _filterMenu.Show(_btnFilter, new Point(0, _btnFilter.Height));
        _miFilterHasSkeleton.Click += (_, _) => SetExclusiveFilter(hasSkeleton: !_filterHasSkeleton, charactersOnly: false);
        _miFilterCharacters.Click += (_, _) => SetExclusiveFilter(hasSkeleton: false, charactersOnly: !_filterCharactersOnly);
        _btnReload.Click += async (_, _) =>
        {
            if (_rootFolder is not null)
            {
                await LoadFolderAsync(_rootFolder);
            }
        };
        _btnExtractSelected.Click += async (_, _) => await ExtractSelectedAsync();
        _btnExtractAll.Click += async (_, _) => await ExtractAllAsync();
        _btnExtractAnimations.Click += async (_, _) => await ExtractWithAnimationsAsync();
        // The animations button follows the extract-selected button's availability everywhere
        // (selection changes, busy state); skeleton availability is checked on click.
        _btnExtractAnimations.Enabled = _btnExtractSelected.Enabled;
        _btnExtractSelected.EnabledChanged += (_, _) => _btnExtractAnimations.Enabled = _btnExtractSelected.Enabled;
        _btnReimportSelected.Click += async (_, _) => await ReimportSelectedAsync();
        _btnCredits.Click += (_, _) => ShowCreditsDialog();
        _btnCheckUpdates.Click += async (_, _) => await CheckForUpdatesAsync(silent: false);
        _btnReportIssue.Click += (_, _) => ShowReportIssueDialog();
        _btnSettings.Click += (_, _) => ShowSettingsDialog();
        _btnCombineParts.CheckedChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            QueueAssetTreeRebuild(resetViewport: true);
        };
        _btnPan.CheckedChanged += (_, _) => _preview.SetPanMode(_btnPan.Checked);
        _btnPose.CheckedChanged += (_, _) =>
        {
            if (_btnPose.Checked)
            {
                _miSkeleton.Checked = true;
            }
            else
            {
                _miSkeleton.Checked = false;
            }

            _preview.SetPoseMode(_btnPose.Checked);
        };
        _miFit.Click += (_, _) => _preview.Fit();
        _miShaded.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.Shaded);
        _miUnlit.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.Unlit);
        _miNoTexture.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.NoTexture);
        _miUvView.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.UvView);
        _miTextureSlotDebug.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.TextureSlotDebug);
        _miNormals.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.Normals);
        _miVertexColor.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.VertexColor);
        _miSkinWeights.Click += (_, _) => ApplyPreviewRenderMode(PreviewRenderMode.SkinWeights);
        _miPolygons.Click += (_, _) =>
        {
            _miPolygons.Checked = !_miPolygons.Checked;
            _preview.TogglePolygons();
        };
        _miSkeleton.Click += (_, _) =>
        {
            _miSkeleton.Checked = !_miSkeleton.Checked;
            if (!_miSkeleton.Checked && _btnPose.Checked)
            {
                _btnPose.Checked = false;
                return;
            }

            _preview.SetSkeletonVisible(_miSkeleton.Checked);
        };
        _miAnimations.Click += async (_, _) => await ShowAnimationPlayerAsync();
        _miTextureProbe.Click += (_, _) =>
        {
            _textureProbeMode = (_textureProbeMode + 1) % 3;
            ApplyTextureProbeMode();
        };
        _tree.AfterSelect += (_, e) => HandleTreeAfterSelect(e.Node);
        _tree.NodeMouseClick += (_, e) => HandleTreeNodeMouseClick(e);
        _tree.AfterExpand += (_, _) => ScheduleTreeAutoFit();
        _tree.AfterCollapse += (_, _) => ScheduleTreeAutoFit();
        _searchDebounceTimer.Interval = 220;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildAssetTree(resetViewport: true);
        };
        _treeFitDebounceTimer.Interval = 180;
        _treeFitDebounceTimer.Tick += (_, _) =>
        {
            _treeFitDebounceTimer.Stop();
            AutoFitTreeWidth();
        };
        _searchText.TextChanged += (_, _) => ScheduleSearchRebuild();

        AllowDrop = true;
        _preview.AllowDrop = true;
        DragEnter += (_, e) => HandleDragEnter(e);
        DragOver += (_, e) => HandleDragEnter(e);
        DragLeave += (_, _) => _preview.SetDragDropHintVisible(false);
        DragDrop += async (_, e) => await HandleDragDropAsync(e);
        _preview.DragEnter += (_, e) => HandleDragEnter(e);
        _preview.DragOver += (_, e) => HandleDragEnter(e);
        _preview.DragLeave += (_, _) => _preview.SetDragDropHintVisible(false);
        _preview.DragDrop += async (_, e) => await HandleDragDropAsync(e);
    }

    private void ApplyTextureProbeMode()
    {
        switch (_textureProbeMode)
        {
            case 1:
                _miTextureProbe.Text = Loc.T("view.texture_probe.normal");
                _miTextureProbe.Checked = true;
                _preview.SetTextureProbeLiveHover(false);
                _preview.SetTextureProbeEnabled(true);
                break;
            case 2:
                _miTextureProbe.Text = Loc.T("view.texture_probe.live");
                _miTextureProbe.Checked = true;
                _preview.SetTextureProbeEnabled(true);
                _preview.SetTextureProbeLiveHover(true);
                break;
            default:
                _textureProbeMode = 0;
                _miTextureProbe.Text = Loc.T("view.texture_probe");
                _miTextureProbe.Checked = false;
                _preview.SetTextureProbeLiveHover(false);
                _preview.SetTextureProbeEnabled(false);
                break;
        }
    }

    private void ApplyPreviewRenderMode(PreviewRenderMode mode)
    {
        _miShaded.Checked = mode == PreviewRenderMode.Shaded;
        _miUnlit.Checked = mode == PreviewRenderMode.Unlit;
        _miNoTexture.Checked = mode == PreviewRenderMode.NoTexture;
        _miUvView.Checked = mode == PreviewRenderMode.UvView;
        _miTextureSlotDebug.Checked = mode == PreviewRenderMode.TextureSlotDebug;
        _miNormals.Checked = mode == PreviewRenderMode.Normals;
        _miVertexColor.Checked = mode == PreviewRenderMode.VertexColor;
        _miSkinWeights.Checked = mode == PreviewRenderMode.SkinWeights;
        _preview.SetRenderMode(mode);
    }

    private void HandleDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            _preview.SetDragDropHintVisible(true);
        }
        else
        {
            e.Effect = DragDropEffects.None;
            _preview.SetDragDropHintVisible(false);
        }
    }

    private async Task HandleDragDropAsync(DragEventArgs e)
    {
        _preview.SetDragDropHintVisible(false);

        var paths = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var path = paths[0];
        if (Directory.Exists(path))
        {
            await LoadFolderAsync(path);
        }
        else if (File.Exists(path))
        {
            if (path.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase))
            {
                await OpenMeshFileAsync(path);
            }
            else
            {
                await LoadFolderAsync(Path.GetDirectoryName(path)!);
            }
        }
    }

    private async Task OpenMeshFileAsync(string meshPath)
    {
        if (!File.Exists(meshPath))
        {
            MessageBox.Show(
                Loc.T("msg.mesh_not_found", meshPath),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await LoadFolderAsync(Path.GetDirectoryName(meshPath)!);
        SelectLoadedMesh(meshPath);
    }

    private void UpdateFormatButton()
    {
        var format = _exportFormat == ExportFormat.Glb ? "GLB" : "GLTF";
        var atlas = _btnDiffuseAtlas.Checked ? Loc.T("common.on") : Loc.T("common.off");
        var aa = _viewerAntiAliasing ? Loc.T("common.on") : Loc.T("common.off");
        var camera = _viewerFlightCamera ? Loc.T("settings.camera.flight") : Loc.T("settings.camera.orbit");
        var faceBones = _normalizeFacialBonesOnReimport ? Loc.T("common.on") : Loc.T("common.off");
        var discord = _discordRichPresence ? Loc.T("common.on") : Loc.T("common.off");
        _btnSettings.ToolTipText = Loc.T("settings.tooltip_summary", format, atlas, faceBones, aa, camera, discord);
    }

    private void ShowSettingsDialog()
    {
        using var dialog = new Form
        {
            Text = Loc.T("settings.title"),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Language picker lives at the top of the General tab (above Output Format). It is populated from
        // the Languages/ folder next to the executable, so community-contributed translations show up here
        // automatically. Switching language takes effect after a restart (offered below on OK).
        var languageLabel = new Label
        {
            Text = Loc.T("settings.language_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var languageCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 10),
        };
        var availableLanguages = Loc.AvailableLanguages;
        foreach (var lang in availableLanguages)
        {
            languageCombo.Items.Add(lang);
        }
        var currentLanguageIndex = -1;
        for (var i = 0; i < availableLanguages.Count; i++)
        {
            if (availableLanguages[i].Code.Equals(Loc.CurrentCode, StringComparison.OrdinalIgnoreCase))
            {
                currentLanguageIndex = i;
                break;
            }
        }
        if (currentLanguageIndex >= 0)
        {
            languageCombo.SelectedIndex = currentLanguageIndex;
        }

        var formatLabel = new Label
        {
            Text = Loc.T("settings.output_format"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var formatCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 10),
        };
        formatCombo.Items.Add("GLB");
        formatCombo.Items.Add("GLTF");
        formatCombo.SelectedIndex = _exportFormat == ExportFormat.Glb ? 0 : 1;

        var atlasLabel = new Label
        {
            Text = Loc.T("settings.texture_atlas_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var atlasCheck = new CheckBox
        {
            Text = Loc.T("settings.generate_texture_atlas"),
            Checked = _btnDiffuseAtlas.Checked,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };

        var compressionLabel = new Label
        {
            Text = Loc.T("settings.textures_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var uncompressedCheck = new CheckBox
        {
            Text = Loc.T("settings.uncompressed_textures"),
            Checked = _uncompressedTextures,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };

        var toolTip = new ToolTip();
        var scaleLabel = new Label
        {
            Text = Loc.T("settings.model_size_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var matchSizeCheck = new CheckBox
        {
            Text = Loc.T("settings.match_size"),
            Checked = _matchOriginalModelSize,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };
        toolTip.SetToolTip(matchSizeCheck, Loc.T("settings.match_size.tooltip"));

        var facialBonesLabel = new Label
        {
            Text = Loc.T("settings.facial_bones_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var facialBonesCheck = new CheckBox
        {
            Text = Loc.T("settings.normalize_facial_bones"),
            Checked = _normalizeFacialBonesOnReimport,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };
        toolTip.SetToolTip(facialBonesCheck, Loc.T("settings.normalize_facial_bones.tooltip"));

        var viewerLabel = new Label
        {
            Text = Loc.T("settings.viewer_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var antiAliasCheck = new CheckBox
        {
            Text = Loc.T("settings.anti_aliasing"),
            Checked = _viewerAntiAliasing,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };
        toolTip.SetToolTip(antiAliasCheck, Loc.T("settings.anti_aliasing.tooltip"));

        var cameraLabel = new Label
        {
            Text = Loc.T("settings.camera_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var flightCameraCheck = new CheckBox
        {
            Text = Loc.T("settings.flight_camera"),
            Checked = _viewerFlightCamera,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };
        toolTip.SetToolTip(flightCameraCheck, Loc.T("settings.flight_camera.tooltip"));

        var integrationLabel = new Label
        {
            Text = Loc.T("settings.discord_label"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var discordPresenceCheck = new CheckBox
        {
            Text = Loc.T("settings.discord_presence"),
            Checked = _discordRichPresence,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };
        toolTip.SetToolTip(discordPresenceCheck, Loc.T("settings.discord_presence.tooltip"));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 0),
        };
        var ok = new Button { Text = Loc.T("common.ok"), DialogResult = DialogResult.OK, Width = 86 };
        var cancel = new Button { Text = Loc.T("common.cancel"), DialogResult = DialogResult.Cancel, Width = 86 };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
#if DEBUG
        // Debug-only: pull the latest release and show the update dialog as if an update had arrived, so
        // the update UI (HTML rendering, buttons) can be tested without publishing a newer version.
        var simulateUpdate = new Button { Text = Loc.T("settings.simulate_update"), AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
        simulateUpdate.Click += async (_, _) => await SimulateUpdateAsync();
        buttons.Controls.Add(simulateUpdate);
        var simulateDiscordInvite = new Button { Text = Loc.T("settings.simulate_discord_invite"), AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
        simulateDiscordInvite.Click += (_, _) => SimulateDiscordInvite();
        buttons.Controls.Add(simulateDiscordInvite);
#endif

        var tabs = new TabControl
        {
            Margin = new Padding(0),
        };
        var generalPage = new TabPage(Loc.T("settings.tab.general"))
        {
            Padding = new Padding(10),
            UseVisualStyleBackColor = true,
        };
        var viewerPage = new TabPage(Loc.T("settings.tab.viewer"))
        {
            Padding = new Padding(10),
            UseVisualStyleBackColor = true,
        };
        var integrationsPage = new TabPage(Loc.T("settings.tab.integrations"))
        {
            Padding = new Padding(10),
            UseVisualStyleBackColor = true,
        };

        var generalLayout = CreateSettingsPageLayout();
        generalLayout.Controls.Add(languageLabel, 0, 0);
        generalLayout.Controls.Add(languageCombo, 1, 0);
        generalLayout.Controls.Add(formatLabel, 0, 1);
        generalLayout.Controls.Add(formatCombo, 1, 1);
        generalLayout.Controls.Add(atlasLabel, 0, 2);
        generalLayout.Controls.Add(atlasCheck, 1, 2);
        generalLayout.Controls.Add(compressionLabel, 0, 3);
        generalLayout.Controls.Add(uncompressedCheck, 1, 3);
        generalLayout.Controls.Add(scaleLabel, 0, 4);
        generalLayout.Controls.Add(matchSizeCheck, 1, 4);
        generalLayout.Controls.Add(facialBonesLabel, 0, 5);
        generalLayout.Controls.Add(facialBonesCheck, 1, 5);

        var viewerLayout = CreateSettingsPageLayout();
        viewerLayout.Controls.Add(viewerLabel, 0, 0);
        viewerLayout.Controls.Add(antiAliasCheck, 1, 0);
        viewerLayout.Controls.Add(cameraLabel, 0, 1);
        viewerLayout.Controls.Add(flightCameraCheck, 1, 1);

        var integrationsLayout = CreateSettingsPageLayout();
        integrationsLayout.Controls.Add(integrationLabel, 0, 0);
        integrationsLayout.Controls.Add(discordPresenceCheck, 1, 0);

        generalPage.Controls.Add(generalLayout);
        viewerPage.Controls.Add(viewerLayout);
        integrationsPage.Controls.Add(integrationsLayout);
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(viewerPage);
        tabs.TabPages.Add(integrationsPage);

        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        // Size the tab area to the largest page so labels and checkboxes are never clipped in any language
        // (label/checkbox text length varies a lot between translations). Measured after parenting so the
        // controls use the dialog font, and the auto-sizing dialog grows to fit the result.
        var pageLayouts = new[] { generalLayout, viewerLayout, integrationsLayout };
        var contentWidth = pageLayouts.Max(page => page.PreferredSize.Width);
        var contentHeight = pageLayouts.Max(page => page.PreferredSize.Height);
        tabs.Size = new Size(Math.Max(contentWidth + 40, 360), contentHeight + 62);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _exportFormat = formatCombo.SelectedIndex == 0 ? ExportFormat.Glb : ExportFormat.GltfSeparate;
        _btnDiffuseAtlas.Checked = atlasCheck.Checked;
        _uncompressedTextures = uncompressedCheck.Checked;
        _matchOriginalModelSize = matchSizeCheck.Checked;
        _normalizeFacialBonesOnReimport = facialBonesCheck.Checked;
        _viewerAntiAliasing = antiAliasCheck.Checked;
        _viewerFlightCamera = flightCameraCheck.Checked;
        _discordRichPresence = discordPresenceCheck.Checked;
        _preview.SetAntiAliasing(_viewerAntiAliasing);
        _preview.SetCameraMode(_viewerFlightCamera ? PreviewCameraMode.Flight : PreviewCameraMode.Orbit);
        _discordPresence.SetEnabled(_discordRichPresence);
        UpdateDiscordPresence();
        UpdateFormatButton();
        AppPreferences.SaveToolSettings(
            _exportFormat == ExportFormat.Glb ? "Glb" : "GltfSeparate",
            _btnDiffuseAtlas.Checked,
            _uncompressedTextures,
            _matchOriginalModelSize,
            _normalizeFacialBonesOnReimport,
            _viewerAntiAliasing,
            _viewerFlightCamera,
            _discordRichPresence);

        // Language change is handled separately: the UI is built once at startup, so switching takes
        // effect after a restart. Save the choice and offer to restart now.
        if (languageCombo.SelectedItem is LanguageInfo chosenLanguage &&
            !chosenLanguage.Code.Equals(Loc.CurrentCode, StringComparison.OrdinalIgnoreCase))
        {
            AppPreferences.SaveLanguage(chosenLanguage.Code);
            var restart = MessageBox.Show(
                Loc.T("settings.language_restart.body"),
                Loc.T("settings.language_restart.title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (restart == DialogResult.Yes)
            {
                Application.Restart();
            }
        }
    }

    private static TableLayoutPanel CreateSettingsPageLayout()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,
        };
        // The label column auto-sizes to the widest label so translations (which vary a lot in length)
        // are never clipped; the second column takes the rest for the combos/checkboxes.
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return layout;
    }

    private void SetReadyState()
    {
        _btnExtractSelected.Enabled = false;
        _btnExtractAll.Enabled = false;
        _btnReimportSelected.Enabled = false;
        _btnCombineParts.Enabled = false;
        _btnFilter.Enabled = false;
        _btnReload.Enabled = false;
        _searchText.Enabled = false;
        SetStatusText(Loc.T("status.ready"));
        SetDetailText("");
    }

    private static string BuildLoadedDetailText(int modelCount, int groupCount)
    {
        var models = modelCount == 1 ? Loc.T("detail.one_model") : Loc.T("detail.n_models", modelCount);
        var groups = groupCount == 1 ? Loc.T("detail.one_group") : Loc.T("detail.n_groups", groupCount);
        return Loc.T("detail.loaded_summary", models, groups);
    }

    private void SetStatusText(string text)
    {
        _statusLabel.Text = text;
        _statusLabel.ToolTipText = text;
    }

    private void SetDetailText(string text)
    {
        _detailLabel.Text = text;
        _detailLabel.ToolTipText = text;
    }

    private async Task OpenFolderDialogAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("dialog.open_folder.description"),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await LoadFolderAsync(dialog.SelectedPath);
        }
    }

    // Opens one or more Telltale containers (.ttarch / .ttarch2), extracts the editor-relevant
    // assets, and loads them through the normal folder-loading path. Selecting several archives at
    // once is useful because games like The Wolf Among Us split meshes and their textures across
    // separate archives (e.g. "..._mesh.ttarch2" + "..._tx.ttarch2"); extracting them together lets
    // a model show up with its textures. No external unpacking tool is needed.
    private async Task OpenArchiveAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("dialog.open_archive.title"),
            Filter = Loc.T("dialog.open_archive.filter"),
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await ExtractArchivesAsync(dialog.FileNames);
    }

    // Set by Program.cs when the app was relaunched elevated to extract inside a protected folder.
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string[]? PendingArchivePaths { get; set; }

    private async Task ExtractArchivesAsync(string[] archivePaths)
    {
        var baseDir = Path.GetDirectoryName(archivePaths[0]) ?? Environment.CurrentDirectory;
        // Game installs often live under protected folders (C:\Program Files (x86)\Telltale Games\...),
        // where creating the "<archive>_extracted" folder throws Access Denied unless elevated. Ask the
        // user whether to restart elevated (extract next to the game) or use a writable fallback folder.
        if (!IsDirectoryWritable(baseDir))
        {
            var fallbackDir = ResolveWritableExtractionBaseDir(baseDir);
            var choice = MessageBox.Show(
                this,
                Loc.T("msg.archive.protected_folder", baseDir, fallbackDir),
                Text,
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel)
            {
                return;
            }

            if (choice == DialogResult.Yes)
            {
                // Relaunch elevated and let the new instance resume this exact action.
                var arguments = "--open-archives " + string.Join(" ", archivePaths.Select(p => $"\"{p}\""));
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = arguments,
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                    Application.Exit();
                    return;
                }
                catch
                {
                    // The UAC prompt was declined; fall back to the writable folder.
                }
            }

            baseDir = fallbackDir;
        }

        // Group archives by their scene/episode "section" (Boot, Fables101, Menu...) instead of by raw
        // file name. Every archive of the same section extracts into one section-named subfolder, and
        // the shared parent folder is loaded, so the viewer shows one tidy group per scene with each
        // mesh sitting next to the textures from that scene's other archives (data / mesh / tx / txmesh).
        var single = archivePaths.Length == 1;
        var loadFolder = single
            ? Path.Combine(baseDir, Path.GetFileNameWithoutExtension(archivePaths[0]) + "_extracted")
            : Path.Combine(baseDir, "ttarch_extracted");

        var totalExtracted = 0;
        var failures = new List<string>();
        var detectedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await RunWithUiLockAsync(async () =>
        {
            SetStatusText(archivePaths.Length == 1
                ? Loc.T("status.extracting_archive")
                : Loc.T("status.extracting_n_archives", archivePaths.Length));

            IProgress<int> archiveProgress = new Progress<int>(done =>
                SetProgress(done, archivePaths.Length, Loc.T("status.extracting_archive_progress", Math.Min(done + 1, archivePaths.Length), archivePaths.Length)));
            await Task.Run(() =>
            {
                for (var i = 0; i < archivePaths.Length; i++)
                {
                    var archivePath = archivePaths[i];
                    archiveProgress.Report(i);

                    // Single archive: extract straight into its own folder. Multiple: one subfolder
                    // per section (Boot, Fables101, ...) so same-scene archives merge together.
                    var destFolder = single
                        ? loadFolder
                        : Path.Combine(loadFolder, ArchiveImporter.GetSectionName(archivePath));
                    try
                    {
                        var result = ArchiveImporter.Extract(archivePath, destFolder);
                        totalExtracted += result.ExtractedCount;
                        detectedGames.Add(result.DetectedGame);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(archivePath)}: {ex.Message}");
                    }

                    archiveProgress.Report(i + 1);
                }
            });

            if (totalExtracted > 0)
            {
                TryAutoSelectGameForGeneric(loadFolder, archivePaths.Concat(detectedGames));

                var loadProgress = new Progress<double>(fraction =>
                    SetProgress((int)(fraction * 1000), 1000, Loc.T("status.reading_folder_pct", (int)(fraction * 100))));
                await ReloadAssetsAsync(loadFolder, loadProgress);
            }

            var detected = detectedGames.Count > 0
                ? Loc.T("msg.archive.detected", string.Join(", ", detectedGames))
                : "";
            var message = totalExtracted > 0
                ? Loc.T("msg.archive.extracted", totalExtracted, archivePaths.Length, loadFolder, detected)
                : Loc.T("msg.archive.none");

            if (failures.Count > 0)
            {
                message += Loc.T("msg.archive.failures", failures.Count) + string.Join("\n", failures);
            }

            return message;
        });
    }

    // Template diffuse names per v45 batch (submesh order == batch order). Characters with
    // EXTERNAL materials need their sk*.prop next to the meshes; when the props are missing the
    // parse yields no names, so fall back to the sibling .d3dtx stems (reserving stems that
    // clearly belong to another part, e.g. "_hair" for the hair mesh) and flag it so the report
    // can tell the user to extract with the .prop files included.
    private static (List<string?> Diffuse, bool MissingProps) ResolveV45TemplateDiffuse(string meshPath)
    {
        var names = D3DMeshParser.ParseFile(meshPath).Submeshes
            .Select(sub => sub.TextureNames.TryGetValue("diffuse", out var diffuseName) ? diffuseName : null)
            .ToList();
        if (names.Count > 0 && names.All(name => !string.IsNullOrWhiteSpace(name)))
        {
            return (names, false);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(meshPath));
        if (string.IsNullOrWhiteSpace(folder))
        {
            return (names, true);
        }

        var meshStem = Path.GetFileNameWithoutExtension(meshPath);
        var otherPartStems = Directory.EnumerateFiles(folder, "*.d3dmesh")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => stem is not null && !stem.Equals(meshStem, StringComparison.OrdinalIgnoreCase))
            .Select(stem => stem!)
            .ToList();

        static int SharedPrefixLength(string a, string b)
        {
            var max = Math.Min(a.Length, b.Length);
            var i = 0;
            while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
            {
                i++;
            }

            return i;
        }

        var candidates = Directory.EnumerateFiles(folder, "*.d3dtx")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => stem is not null)
            .Select(stem => stem!)
            .Where(stem => !stem.StartsWith("color_", StringComparison.OrdinalIgnoreCase) &&
                           !stem.EndsWith("_nm", StringComparison.OrdinalIgnoreCase) &&
                           SharedPrefixLength(stem, meshStem) >= 6)
            // A texture whose stem matches ANOTHER part's stem (skM1_lukas100_hair.d3dtx vs the
            // hair mesh) belongs to that part — unless it also matches this one.
            .Where(stem => SharedPrefixLength(stem, meshStem) >= stem.Length ||
                           !otherPartStems.Any(other =>
                               other.EndsWith(stem[SharedPrefixLength(stem, meshStem)..], StringComparison.OrdinalIgnoreCase) &&
                               SharedPrefixLength(stem, other) > SharedPrefixLength(stem, meshStem)))
            .OrderByDescending(stem => SharedPrefixLength(stem, meshStem))
            .ThenBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var used = new HashSet<string>(names.Where(name => !string.IsNullOrWhiteSpace(name))!, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(names[i]))
            {
                continue;
            }

            names[i] = candidates.FirstOrDefault(candidate => used.Add(candidate));
        }

        return (names, true);
    }

    // Returns the folder itself when writable; otherwise a stable per-game folder under
    // %LOCALAPPDATA%\TelltaleD3DMeshEditor\extracted, named after the last two path segments
    // (e.g. "Minecraft Story Mode - Season Two_Archives") plus a short hash of the full path so
    // two installs with the same folder names never collide. Stable naming means re-opening
    // archives from the same game keeps merging into the same extraction folder.
    private static string ResolveWritableExtractionBaseDir(string baseDir)
    {
        if (IsDirectoryWritable(baseDir))
        {
            return baseDir;
        }

        static string Sanitize(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name.Trim();
        }

        var segments = baseDir
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(2)
            .Select(Sanitize)
            .Where(segment => segment.Length > 0)
            .ToArray();
        var label = segments.Length > 0 ? string.Join("_", segments) : "archives";
        var hash = Crc64Ecma.Compute(baseDir.ToLowerInvariant()) & 0xFFFFFFFF;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelltaleD3DMeshEditor",
            "extracted",
            $"{label}_{hash:X8}");
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, ".write_probe_" + Guid.NewGuid().ToString("N")[..8]);
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateGameSelector()
    {
        _gameSelector.Text = Loc.T("game_selector.text", GameConfig.Current.DisplayName);
        if (TryGetGameMenuImage(GameConfig.Current.Id, out var image))
        {
            _gameSelector.Image = image;
            _gameSelector.ImageScaling = ToolStripItemImageScaling.None;
        }
        else
        {
            _gameSelector.Image = null;
        }

        UpdateGameSelectorImages(_gameSelector.DropDownItems);
        UpdateGameSelectorChecks(_gameSelector.DropDownItems);
    }

    private void BuildGameSelectorMenu()
    {
        _gameSelector.DropDownItems.Clear();
        foreach (var game in GameConfig.All)
        {
            if (game.Id is GameId.BackToTheFutureEpisode1 or
                GameId.BackToTheFutureEpisode2 or
                GameId.BackToTheFutureEpisode3 or
                GameId.BackToTheFutureEpisode4 or
                GameId.BackToTheFutureEpisode5 or
                GameId.WalkingDeadSeason2 or
                GameId.WalkingDeadMichonne or
                GameId.MinecraftStoryMode or
                GameId.MinecraftStoryModeSeason2 or
                GameId.TalesFromTheBorderlands2014 or
                GameId.TalesFromTheBorderlandsE3 or
                GameId.TalesFromTheBorderlandsOld)
            {
                continue;
            }

            var item = CreateGameMenuItem(game, selectable: !game.IsGameMenuGroup);
            if (game.Id == GameId.TalesFromTheBorderlands)
            {
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.TalesFromTheBorderlands2014));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.TalesFromTheBorderlandsE3));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.TalesFromTheBorderlandsOld));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.TalesFromTheBorderlands2021, enabled: false));
            }

            if (game.Id == GameId.BackToTheFuture)
            {
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.BackToTheFutureEpisode1));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.BackToTheFutureEpisode2, enabled: false));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.BackToTheFutureEpisode3, enabled: false));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.BackToTheFutureEpisode4, enabled: false));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.BackToTheFutureEpisode5, enabled: false));
            }

            if (game.Id == GameId.WalkingDead)
            {
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.WalkingDeadSeason2));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.WalkingDeadMichonne));
            }

            if (game.Id == GameId.MinecraftStoryModeGroup)
            {
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.MinecraftStoryMode));
                item.DropDownItems.Add(CreateGameMenuItem(GameConfig.MinecraftStoryModeSeason2));
            }

            _gameSelector.DropDownItems.Add(item);
        }
    }

    private ToolStripMenuItem CreateGameMenuItem(GameConfig game, bool selectable = true, bool enabled = true)
    {
        var item = new ToolStripMenuItem(game.DisplayName) { Tag = game, Enabled = enabled };
        if (TryGetGameMenuImage(game.Id, out var image))
        {
            item.Image = image;
            item.ImageScaling = ToolStripItemImageScaling.None;
        }

        if (selectable && enabled)
        {
            item.Click += async (_, _) => await SelectGameAsync(game);
        }

        return item;
    }

    private bool TryGetGameMenuImage(GameId id, out Image image)
    {
        image = null!;
        var imageId = ResolveGameMenuImageId(id);
        var resourceName = imageId switch
        {
            GameId.Generic => "Auto-Generic",
            GameId.WalkingDead => "TWD",
            GameId.WalkingDeadSeason2 => "TWDS2",
            GameId.WalkingDeadMichonne => "TWDM",
            GameId.WolfAmongUs => "TWAU",
            GameId.MinecraftStoryModeGroup => "MCSM",
            GameId.MinecraftStoryMode => "MCSMS1",
            GameId.MinecraftStoryModeSeason2 => "MCSMS2",
            GameId.TalesFromTheBorderlands => "TFTB",
            GameId.TalesFromTheBorderlands2014 => "TFTB2014",
            GameId.TalesFromTheBorderlandsE3 => "TFTBE3",
            GameId.TalesFromTheBorderlandsOld => "TFTBOLD",
            GameId.TalesFromTheBorderlands2021 => "TFTB2021",
            GameId.GameOfThrones => "GOT",
            GameId.Batman => "BAT",
            GameId.BackToTheFuture => "BTTF",
            GameId.BackToTheFutureEpisode1 => "BTTF101",
            GameId.BackToTheFutureEpisode2 => "BTTF102",
            GameId.BackToTheFutureEpisode3 => "BTTF103",
            GameId.BackToTheFutureEpisode4 => "BTTF104",
            GameId.BackToTheFutureEpisode5 => "BTTF105",
            _ => ""
        };
        if (string.IsNullOrEmpty(resourceName))
        {
            return false;
        }

        if (_gameMenuImages.TryGetValue(imageId, out image!))
        {
            return true;
        }

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"TelltaleD3DMeshEditor.Resources.Images.{resourceName}.png");
        if (stream is null)
        {
            return false;
        }

        using var loaded = Image.FromStream(stream);
        image = CreateMenuThumbnail(loaded, 28);
        _gameMenuImages[imageId] = image;
        return true;
    }

    private static GameId ResolveGameMenuImageId(GameId id)
        => id switch
        {
            GameId.TalesFromTheBorderlands when GameConfig.Current.IsTalesFromTheBorderlands => GameConfig.Current.Id,
            GameId.BackToTheFuture when IsBackToTheFutureEpisode(GameConfig.Current.Id) => GameConfig.Current.Id,
            _ => id,
        };

    private static bool IsBackToTheFutureEpisode(GameId id)
        => id is GameId.BackToTheFutureEpisode1 or
                 GameId.BackToTheFutureEpisode2 or
                 GameId.BackToTheFutureEpisode3 or
                 GameId.BackToTheFutureEpisode4 or
                 GameId.BackToTheFutureEpisode5;

    private static Bitmap CreateMenuThumbnail(Image source, int size)
    {
        var thumbnail = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        return thumbnail;
    }

    private static void UpdateGameSelectorChecks(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            item.Checked = item.Tag is GameConfig game &&
                (ReferenceEquals(game, GameConfig.Current) ||
                 (game.Id == GameId.WalkingDead && GameConfig.Current.IsWalkingDead) ||
                 (game.Id == GameId.MinecraftStoryModeGroup && GameConfig.Current.IsMinecraftStoryMode) ||
                 (game.Id == GameId.TalesFromTheBorderlands && GameConfig.Current.IsTalesFromTheBorderlands) ||
                 (game.Id == GameId.BackToTheFuture && GameConfig.Current.IsBackToTheFuture));
            UpdateGameSelectorChecks(item.DropDownItems);
        }
    }

    private void UpdateGameSelectorImages(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is GameConfig game && TryGetGameMenuImage(game.Id, out var image))
            {
                item.Image = image;
                item.ImageScaling = ToolStripItemImageScaling.None;
            }
            else
            {
                item.Image = null;
            }

            UpdateGameSelectorImages(item.DropDownItems);
        }
    }

    // Switches the active game and re-loads the current folder so textures and groups are re-resolved
    // under the new game's rules.
    private async Task SelectGameAsync(GameConfig game)
    {
        if (ReferenceEquals(GameConfig.Current, game))
        {
            return;
        }

        GameConfig.Current = game;
        AppPreferences.SaveGameConfig(game);
        UpdateGameSelector();
        UpdateSelectionButtons();
        UpdateDiscordPresence();

        if (_rootFolder is not null)
        {
            await LoadFolderAsync(_rootFolder);
        }
    }

    private void TryAutoSelectGameForGeneric(string folder, IEnumerable<string>? extraHints = null)
    {
        if (GameConfig.Current.Id != GameId.Generic)
        {
            return;
        }

        var inferred = InferGameConfigForAutoGeneric(folder, extraHints);
        if (inferred is null || inferred.Id == GameId.Generic)
        {
            return;
        }

        GameConfig.Current = inferred;
        AppPreferences.SaveGameConfig(inferred);
        UpdateGameSelector();
        UpdateSelectionButtons();
        UpdateDiscordPresence();
    }

    private static GameConfig? InferGameConfigForAutoGeneric(string folder, IEnumerable<string>? extraHints)
    {
        foreach (var hint in EnumerateAutoGenericTextHints(folder, extraHints))
        {
            var inferred = InferGameConfigFromText(hint);
            if (inferred is not null)
            {
                return inferred;
            }
        }

        foreach (var assetPath in EnumerateAutoGenericAssetFiles(folder))
        {
            var inferred = InferGameConfigFromAssetHeader(assetPath);
            if (inferred is not null)
            {
                return inferred;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAutoGenericTextHints(string folder, IEnumerable<string>? extraHints)
    {
        yield return folder;

        if (extraHints is not null)
        {
            foreach (var hint in extraHints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    yield return hint;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateAutoGenericAssetFiles(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    path.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static GameConfig? InferGameConfigFromAssetHeader(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var header = MetaStreamHeader.Parse(data);
            if (header.DataOffset <= 0 || header.DataOffset + 12 > data.Length)
            {
                return null;
            }

            var reader = new DataReader(data);
            reader.Seek(header.DataOffset);
            var nameHeaderLength = reader.ReadUInt32();
            var nameLength = reader.ReadUInt32();
            if (nameLength > nameHeaderLength)
            {
                reader.Seek(reader.Position - 4);
                nameLength = nameHeaderLength;
            }

            if (nameLength > data.Length || reader.Position + nameLength + 4 > data.Length)
            {
                return null;
            }

            reader.Skip(checked((int)nameLength));
            var meshVersion = reader.ReadInt32();
            return InferGameConfigFromMeshSignature(header.Version, meshVersion);
        }
        catch
        {
            return null;
        }
    }

    private static GameConfig? InferGameConfigFromMeshSignature(string metaStreamVersion, int meshVersion)
    {
        if (metaStreamVersion == "MSV4")
        {
            return GameConfig.TalesFromTheBorderlandsOld;
        }

        if (metaStreamVersion == "MTRE")
        {
            if (meshVersion == 1)
            {
                return GameConfig.BackToTheFutureEpisode1;
            }

            if (meshVersion is 5 or 6 or 9 or 10 or 11)
            {
                return GameConfig.TalesFromTheBorderlandsOld;
            }

            return null;
        }

        if (metaStreamVersion == "MSV5" && meshVersion == 12)
        {
            return GameConfig.TalesFromTheBorderlandsOld;
        }

        if (metaStreamVersion == "MSV5" && meshVersion == 17)
        {
            return GameConfig.TalesFromTheBorderlands2014;
        }

        if (metaStreamVersion == "MSV5" && meshVersion == 18)
        {
            return GameConfig.MinecraftStoryMode;
        }

        if (metaStreamVersion == "MSV5" && meshVersion == 25)
        {
            return GameConfig.WalkingDeadMichonne;
        }

        // Batman: The Telltale Series ships MSV6 v46 meshes (the Telltale "GotG" family container).
        if (metaStreamVersion == "MSV6" && meshVersion == 46)
        {
            return GameConfig.Batman;
        }

        // Minecraft: Story Mode - Season 2 ships MSV5 v45 meshes (parsed via the Telltale Toolkit).
        if (metaStreamVersion == "MSV5" && meshVersion == 45)
        {
            return GameConfig.MinecraftStoryModeSeason2;
        }

        // Versions 13/14 are shared by nearby Telltale generations, and MTRE v12 appears in both
        // supported and unsupported games. Keep Auto / Generic unless the folder/archive name supplied
        // a stronger hint.
        return null;
    }

    private static GameConfig? InferGameConfigFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains("TWDS2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead: Season 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead Season 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WD200", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WalkingDeadSeason2;
        }

        if (text.Contains("TWDM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead: Michonne", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Walking Dead Michonne", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Michonne", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WalkingDeadMichonne;
        }

        if (text.Contains("TWAU", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wolf Among Us", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Fables_pc_", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.WolfAmongUs;
        }

        // Season 2 hints must be checked before the generic MCSM hint below ("MCSM2" contains "MCSM").
        if (text.Contains("MCSM2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MCSMS2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MC2_", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft Story Mode Season 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft: Story Mode - Season 2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MCSM Season 2", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.MinecraftStoryModeSeason2;
        }

        if (text.Contains("MCSM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft Story Mode", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Minecraft: Story Mode", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.MinecraftStoryMode;
        }

        if (IsGameOfThronesHint(text))
        {
            return GameConfig.GameOfThrones;
        }

        if (text.Contains("Batman", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BAT _", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BAT_", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.Batman;
        }

        if (IsTalesFromTheBorderlandsOldHint(text))
        {
            return GameConfig.TalesFromTheBorderlandsOld;
        }

        if (IsTalesFromTheBorderlandsE3Hint(text))
        {
            return GameConfig.TalesFromTheBorderlandsE3;
        }

        if (IsTalesFromTheBorderlands2014Hint(text))
        {
            return GameConfig.TalesFromTheBorderlands2014;
        }

        if (text.Contains("BTTF", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Back to the Future", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BackToTheFuture", StringComparison.OrdinalIgnoreCase))
        {
            return GameConfig.BackToTheFutureEpisode1;
        }

        return null;
    }

    private static bool IsTalesFromTheBorderlands2014Hint(string text)
    {
        if (text.Contains("2021", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Contains("TFTB2014", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TftBL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Tales from the Borderlands", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Borderlands", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGameOfThronesHint(string text)
        => text.Contains("Game of Thrones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GameOfThrones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("Telltale Games Series", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GOT _", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("GOT_", StringComparison.OrdinalIgnoreCase);

    private static bool IsTalesFromTheBorderlandsOldHint(string text)
        => !text.Contains("2021", StringComparison.OrdinalIgnoreCase) &&
           (text.Contains("Source Code Leaked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TFTBOLD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tales from the Borderlands (Old)", StringComparison.OrdinalIgnoreCase));

    private static bool IsTalesFromTheBorderlandsE3Hint(string text)
        => !text.Contains("2021", StringComparison.OrdinalIgnoreCase) &&
           (text.Contains("TFTBE3", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("E3 Leak", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TFTB E3", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tales from the Borderlands E3", StringComparison.OrdinalIgnoreCase));

    // Entry point for Open Folder / Reload / drag-drop. Owns the busy state, the progress bar and error
    // reporting so a large folder loads on a background thread without freezing the window.
    private async Task LoadFolderAsync(string folder)
    {
        SetBusy(true);
        try
        {
            SetProgress(0, 1000, Loc.T("status.reading_folder_pct", 0));
            var progress = new Progress<double>(fraction =>
                SetProgress((int)(fraction * 1000), 1000, Loc.T("status.reading_folder_pct", (int)(fraction * 100))));
            await ReloadAssetsAsync(folder, progress);
        }
        catch (Exception ex)
        {
            SetStatusText(Loc.T("status.error"));
            var logPath = ErrorLog.Write(ex, "Folder load failed");
            MessageBox.Show(
                Loc.T("msg.folder_load_failed", logPath, ex.Message),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Runs the heavy discovery off the UI thread, reporting 0..1 progress, then rebuilds the tree and
    // toolbar state on the UI thread. The caller is responsible for the busy state (so it can be reused
    // both standalone and inside the archive-import flow, which already holds the UI lock).
    private async Task ReloadAssetsAsync(string folder, IProgress<double>? progress)
    {
        _rootFolder = folder;
        _selectedAsset = null;
        _selectedGroup = null;
        _preview.SetScene(null, null);
        UpdateDiscordPresence();
        progress?.Report(0);

        TryAutoSelectGameForGeneric(folder);

        var (assets, groups) = await Task.Run(() =>
        {
            var discovered = ModelAsset.Discover(folder, SubRange(progress, 0.0, 0.7));
            var grouped = ModelAssetGroup.Discover(discovered, folder, SubRange(progress, 0.7, 1.0));
            return (discovered, grouped);
        });
        progress?.Report(1);

        _assets = assets;
        _assetGroups = groups;
        _searchText.Enabled = true;
        _btnFilter.Enabled = true;
        _searchDebounceTimer.Stop();
        if (_searchText.TextLength > 0)
        {
            _searchText.Clear();
            _searchDebounceTimer.Stop();
        }

        RebuildAssetTree(resetViewport: true);

        _btnReload.Enabled = true;
        _btnExtractAll.Enabled = _assets.Count > 0;
        _btnCombineParts.Enabled = _assets.Count > 0;
        _btnExtractSelected.Enabled = false;
        _btnReimportSelected.Enabled = false;
        SetDetailText(BuildLoadedDetailText(_assets.Count, _btnCombineParts.Checked ? _assetGroups.Count : 0));
        SetStatusText(_assets.Count == 0
            ? Loc.T("status.no_meshes")
            : BuildLoadedFolderStatus(folder));
    }

    // Forwards a 0..1 sub-progress into a [start, end] slice of an overall 0..1 progress. Forwards
    // synchronously on the caller's (background) thread; the wrapped UI Progress<T> does the marshaling.
    private static IProgress<double>? SubRange(IProgress<double>? target, double start, double end)
    {
        return target is null
            ? null
            : new DelegateProgress(value => target.Report(start + value * (end - start)));
    }

    private sealed class DelegateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private void QueueAssetTreeRebuild(bool resetViewport)
    {
        if (_treeRebuildQueued || _rootFolder is null)
        {
            return;
        }

        _treeRebuildQueued = true;
        BeginInvoke(new Action(() =>
        {
            _treeRebuildQueued = false;
            RebuildAssetTree(resetViewport);
        }));
    }

    private void RebuildAssetTree(bool resetViewport)
    {
        if (_rootFolder is null)
        {
            return;
        }

        // Tree refreshes must never leave the filter button showing a stale/default label.
        UpdateFilterButtonText();

        var query = _searchText.Text.Trim();
        var visibleAssets = string.IsNullOrEmpty(query)
            ? _assets
            : _assets
                .Where(asset => asset.Matches(_rootFolder, query))
                .ToList();
        visibleAssets = ApplyAssetFilters(visibleAssets);
        var visibleGroups = _btnCombineParts.Checked
            ? string.IsNullOrEmpty(query)
                ? _assetGroups
                : _assetGroups
                    .Where(group => group.Matches(_rootFolder, query))
                    .ToList()
            : [];
        if (_btnCombineParts.Checked)
        {
            visibleGroups = ApplyGroupFilters(visibleGroups);
        }
        if (_btnCombineParts.Checked)
        {
            visibleAssets = FilterLooseAssetsForTree(visibleAssets, visibleGroups).ToList();
        }

        ClearTreeSelectionState();
        _selectedAsset = null;
        _selectedGroup = null;
        _btnExtractSelected.Enabled = false;
        _btnReimportSelected.Enabled = false;

        TreeNode? root;
        _applyingTreeSelection = true;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            root = new TreeNode(Path.GetFileName(_rootFolder.TrimEnd('\\', '/')))
            {
                Tag = _rootFolder
            };
            _tree.Nodes.Add(root);
            PopulateAssetTree(root, _rootFolder, visibleAssets, visibleGroups);
            root.Expand();
            if (!string.IsNullOrEmpty(query))
            {
                ExpandAllVisible(root);
            }
        }
        finally
        {
            _tree.EndUpdate();
            _applyingTreeSelection = false;
        }

        if (resetViewport)
        {
            ResetTreeViewport(root, preferFirstResult: !string.IsNullOrEmpty(query));
        }

        var filtersActive = _filterHasSkeleton || _filterCharactersOnly;
        SetDetailText(string.IsNullOrEmpty(query) && !filtersActive
            ? BuildLoadedDetailText(_assets.Count, visibleGroups.Count)
            : Loc.T("detail.filtered_summary", visibleAssets.Count, _assets.Count, visibleGroups.Count, _assetGroups.Count));
        if ((_assets.Count > 0 || _assetGroups.Count > 0) && visibleAssets.Count == 0 && visibleGroups.Count == 0)
        {
            SetStatusText(string.IsNullOrEmpty(query)
                ? Loc.T("status.no_match_filter")
                : Loc.T("status.no_match_query_filter", query));
        }
        else if (!string.IsNullOrEmpty(query))
        {
            SetStatusText(filtersActive ? Loc.T("status.search_filter", query) : Loc.T("status.search", query));
        }
        else if (filtersActive)
        {
            SetStatusText(Loc.T("status.filter_active"));
        }
        else
        {
            SetStatusText(_assets.Count == 0
                ? Loc.T("status.no_meshes")
                : BuildLoadedFolderStatus(_rootFolder));
        }

        EnsureTreePanelWidth();
        ScheduleTreeAutoFit();
    }

    private void SelectLoadedMesh(string meshPath)
    {
        var fullPath = Path.GetFullPath(meshPath);
        var node = FindNodeByMeshPath(_tree.Nodes, fullPath);
        if (node is null)
        {
            SetStatusText(Loc.T("status.loaded_could_not_select", Path.GetFileName(meshPath)));
            return;
        }

        ExpandParents(node);
        ApplyTreeSelection(node, additive: false, range: false);
        node.EnsureVisible();
        SetStatusText(Loc.T("status.opened", Path.GetFileName(meshPath)));
    }

    private static TreeNode? FindNodeByMeshPath(TreeNodeCollection nodes, string meshPath)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is ModelAsset asset &&
                Path.GetFullPath(asset.MeshPath).Equals(meshPath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindNodeByMeshPath(node.Nodes, meshPath);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static void ExpandParents(TreeNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.Expand();
        }
    }

    private void ScheduleSearchRebuild()
    {
        if (_rootFolder is null)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void UpdateFilterButtonText()
    {
        _btnFilter.Text = _filterHasSkeleton
            ? Loc.T("filter.button.skl")
            : _filterCharactersOnly
                ? Loc.T("filter.button.sk")
                : Loc.T("tree.filter");
    }

    private void SetExclusiveFilter(bool hasSkeleton, bool charactersOnly)
    {
        _filterHasSkeleton = hasSkeleton;
        _filterCharactersOnly = charactersOnly;
        _miFilterHasSkeleton.Checked = _filterHasSkeleton;
        _miFilterCharacters.Checked = _filterCharactersOnly;
        UpdateFilterButtonText();
        QueueAssetTreeRebuild(resetViewport: true);
    }

    private List<ModelAsset> ApplyAssetFilters(IEnumerable<ModelAsset> assets)
        => assets
            .Where(asset => !_filterHasSkeleton || !string.IsNullOrWhiteSpace(asset.SkeletonPath))
            .Where(asset => !_filterCharactersOnly || IsCharacterAsset(asset))
            .ToList();

    private List<ModelAssetGroup> ApplyGroupFilters(IEnumerable<ModelAssetGroup> groups)
        => groups
            .Where(group => !_filterHasSkeleton || !string.IsNullOrWhiteSpace(group.SkeletonPath))
            .Where(group => !_filterCharactersOnly || IsCharacterGroup(group))
            .ToList();

    private static bool IsCharacterAsset(ModelAsset asset)
        => Path.GetFileNameWithoutExtension(asset.MeshPath)
            .StartsWith("sk", StringComparison.OrdinalIgnoreCase);

    private static bool IsCharacterGroup(ModelAssetGroup group)
        => Path.GetFileNameWithoutExtension(group.SkeletonPath)
               .StartsWith("sk", StringComparison.OrdinalIgnoreCase) ||
           group.Assets.Any(IsCharacterAsset);

    private static IEnumerable<ModelAsset> FilterLooseAssetsForTree(
        IReadOnlyList<ModelAsset> assets,
        IReadOnlyList<ModelAssetGroup> visibleGroups)
    {
        var hiddenMeshPaths = BuildHiddenLooseMeshPaths(assets, visibleGroups);
        return assets.Where(asset => !hiddenMeshPaths.Contains(asset.MeshPath));
    }

    private static HashSet<string> BuildHiddenLooseMeshPaths(
        IReadOnlyList<ModelAsset> assets,
        IReadOnlyList<ModelAssetGroup> visibleGroups)
    {
        var hidden = visibleGroups
            .SelectMany(group => group.Assets)
            .Select(asset => asset.MeshPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (visibleGroups.Count == 0)
        {
            return hidden;
        }

        // Once a character/skeleton has Combined groups, every individual part of it (the body, the head,
        // and the loose damage pieces such as headDamageBase/headDamageCheekL that the planner leaves out
        // of the clean preset) is hidden from the loose list. With Combine Parts on, the user only wants
        // the Combined entries, not the raw parts repeated underneath them.
        var groupedBuckets = visibleGroups
            .SelectMany(group => group.Assets)
            .Select(BuildSkeletonBucketKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (!string.IsNullOrWhiteSpace(asset.SkeletonPath) &&
                groupedBuckets.Contains(BuildSkeletonBucketKey(asset)))
            {
                hidden.Add(asset.MeshPath);
            }
        }

        return hidden;
    }

    private static string BuildSkeletonBucketKey(ModelAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.SkeletonPath))
        {
            return "";
        }

        var directory = Path.GetDirectoryName(asset.MeshPath) ?? "";
        return Path.GetFullPath(directory) + "\0" + Path.GetFullPath(asset.SkeletonPath);
    }

    private void PopulateAssetTree(
        TreeNode root,
        string folder,
        IReadOnlyList<ModelAsset> assets,
        IReadOnlyList<ModelAssetGroup> groups)
    {
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = root
        };

        foreach (var group in groups)
        {
            var parent = EnsureFolderNode(root, folders, group.RelativeDirectory);
            var groupNode = new TreeNode(group.ToString())
            {
                Tag = group
            };
            foreach (var asset in group.Assets)
            {
                groupNode.Nodes.Add(new TreeNode(Path.GetFileNameWithoutExtension(asset.MeshPath))
                {
                    Tag = asset
                });
            }

            parent.Nodes.Add(groupNode);
        }

        var groupedMeshPaths = groups
            .SelectMany(group => group.Assets)
            .Select(asset => asset.MeshPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (groupedMeshPaths.Contains(asset.MeshPath))
            {
                continue;
            }

            var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(folder, asset.MeshPath)) ?? "";
            var parent = EnsureFolderNode(root, folders, relativeDir);
            var node = new TreeNode(asset.ToString())
            {
                Tag = asset
            };
            parent.Nodes.Add(node);
        }
    }

    private static TreeNode EnsureFolderNode(TreeNode root, Dictionary<string, TreeNode> folders, string relativeDir)
    {
        if (string.IsNullOrEmpty(relativeDir) || relativeDir == ".")
        {
            return root;
        }

        if (folders.TryGetValue(relativeDir, out var existing))
        {
            return existing;
        }

        var currentPath = "";
        var parent = root;
        foreach (var part in relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            currentPath = string.IsNullOrEmpty(currentPath) ? part : Path.Combine(currentPath, part);
            if (!folders.TryGetValue(currentPath, out var node))
            {
                node = new TreeNode(part);
                folders[currentPath] = node;
                parent.Nodes.Add(node);
            }
            parent = node;
        }

        return parent;
    }

    private static void ExpandAllVisible(TreeNode node)
    {
        node.Expand();
        foreach (TreeNode child in node.Nodes)
        {
            ExpandAllVisible(child);
        }
    }

    private void ResetTreeViewport(TreeNode root, bool preferFirstResult)
    {
        if (_tree.Nodes.Count == 0)
        {
            return;
        }

        var top = preferFirstResult
            ? EnumerateVisibleNodes(root.Nodes).FirstOrDefault(IsExtractableNode) ?? root
            : root;
        _tree.TopNode = top;
        _tree.SelectedNode = null;
    }

    private void HandleTreeAfterSelect(TreeNode? node)
    {
        if (_applyingTreeSelection || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0)
        {
            return;
        }

        ApplyTreeSelection(node, additive: false, range: false);
    }

    private void HandleTreeNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _applyingTreeSelection)
        {
            return;
        }

        var additive = (ModifierKeys & Keys.Control) != 0;
        var range = (ModifierKeys & Keys.Shift) != 0;
        if (!additive && !range && _selectedTreeNodes.Count == 1 && ReferenceEquals(_selectedTreeNodes[0], e.Node))
        {
            return;
        }

        ApplyTreeSelection(e.Node, additive, range);
    }

    private void ApplyTreeSelection(TreeNode? node, bool additive, bool range)
    {
        _applyingTreeSelection = true;
        try
        {
            var next = BuildTreeSelection(node, additive, range);
            SetHighlightedTreeNodes(next);
            if (node is not null && IsExtractableNode(node) && (!range && (!additive || _selectedTreeNodes.Contains(node))))
            {
                _selectionAnchorNode = node;
            }

            var previewNode = node is not null && _selectedTreeNodes.Contains(node)
                ? node
                : _selectedTreeNodes.LastOrDefault();
            _tree.SelectedNode = previewNode;
            OnTreeSelect(previewNode);
            UpdateSelectionButtons();
            // Previewing a node updates the status/detail labels; keep the active filter visible too.
            UpdateFilterButtonText();
        }
        finally
        {
            _applyingTreeSelection = false;
        }
    }

    private List<TreeNode> BuildTreeSelection(TreeNode? node, bool additive, bool range)
    {
        if (node is null || !IsExtractableNode(node))
        {
            _selectionAnchorNode = null;
            return [];
        }

        if (range && _selectionAnchorNode is not null && _selectionAnchorNode.TreeView == _tree)
        {
            var visible = EnumerateVisibleNodes(_tree.Nodes).ToList();
            var start = visible.IndexOf(_selectionAnchorNode);
            var end = visible.IndexOf(node);
            if (start >= 0 && end >= 0)
            {
                if (start > end)
                {
                    (start, end) = (end, start);
                }

                var rangeNodes = visible
                    .Skip(start)
                    .Take(end - start + 1)
                    .Where(IsExtractableNode)
                    .ToList();
                if (rangeNodes.Count > 0)
                {
                    return rangeNodes;
                }
            }
        }

        if (additive)
        {
            var next = _selectedTreeNodes
                .Where(selected => selected.TreeView == _tree)
                .ToList();
            var existing = next.FindIndex(selected => ReferenceEquals(selected, node));
            if (existing >= 0)
            {
                next.RemoveAt(existing);
            }
            else
            {
                next.Add(node);
            }

            return next;
        }

        return [node];
    }

    private void SetHighlightedTreeNodes(IReadOnlyList<TreeNode> nodes)
    {
        var next = nodes
            .Where(node => node.TreeView == _tree && IsExtractableNode(node))
            .Distinct()
            .ToList();

        foreach (var node in _selectedTreeNodes.Where(node => !next.Contains(node)))
        {
            ResetTreeNodeHighlight(node);
        }

        _selectedTreeNodes.Clear();
        _selectedTreeNodes.AddRange(next);
        foreach (var node in _selectedTreeNodes)
        {
            node.BackColor = SystemColors.Highlight;
            node.ForeColor = SystemColors.HighlightText;
        }
    }

    private void ClearTreeSelectionState()
    {
        foreach (var node in _selectedTreeNodes)
        {
            ResetTreeNodeHighlight(node);
        }

        _selectedTreeNodes.Clear();
        _selectionAnchorNode = null;
    }

    private static void ResetTreeNodeHighlight(TreeNode node)
    {
        node.BackColor = Color.Empty;
        node.ForeColor = Color.Empty;
    }

    private static bool IsExtractableNode(TreeNode node)
    {
        return node.Tag is ModelAsset or ModelAssetGroup;
    }

    private static IEnumerable<TreeNode> EnumerateVisibleNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            if (!node.IsExpanded)
            {
                continue;
            }

            foreach (var child in EnumerateVisibleNodes(node.Nodes))
            {
                yield return child;
            }
        }
    }

    // Auto / Generic is for best-effort viewing/extraction and has no safe game-specific reinsert path.
    // Tales from the Borderlands (Old) is the 2014 source-code leak: view/extract/GLB only, never
    // reinsertion (there is no playable build to put files back into). Every reimport entry point is
    // gated on this so the feature is fully unavailable while either profile is selected.
    private static bool ReinsertionSupported => GameConfig.Current.Id is not GameId.Generic
        and not GameId.TalesFromTheBorderlandsOld;

    private static string GetReinsertionUnavailableMessage()
        => GameConfig.Current.Id switch
        {
            GameId.Generic => Loc.T("msg.reinsert.disabled_generic"),
            _ => Loc.T("msg.reinsert.disabled_tftb_old"),
        };

    private void OnTreeSelect(TreeNode? node)
    {
        if (node?.Tag is ModelAssetGroup group)
        {
            _selectedAsset = null;
            _selectedGroup = group;
            _btnExtractSelected.Enabled = true;
            _btnReimportSelected.Enabled = ReinsertionSupported;
            UpdateDiscordPresence();
            PreviewAssetGroup(group);
            return;
        }

        if (node?.Tag is not ModelAsset asset)
        {
            _selectedAsset = null;
            _selectedGroup = null;
            _btnExtractSelected.Enabled = false;
            _btnReimportSelected.Enabled = false;
            SetDetailText(BuildLoadedDetailText(_assets.Count, _assetGroups.Count));
            UpdateDiscordPresence();
            return;
        }

        _selectedAsset = asset;
        _selectedGroup = null;
        _btnExtractSelected.Enabled = true;
        _btnReimportSelected.Enabled = ReinsertionSupported;
        UpdateDiscordPresence();
        PreviewAsset(asset);
    }

    private void UpdateDiscordPresence()
    {
        if (_selectedGroup is not null)
        {
            _discordPresence.SetActivity(GameConfig.Current, _selectedGroup.OutputStem, combinedModel: true);
            return;
        }

        if (_selectedAsset is not null)
        {
            _discordPresence.SetActivity(GameConfig.Current, Path.GetFileName(_selectedAsset.MeshPath));
            return;
        }

        _discordPresence.SetActivity(GameConfig.Current, null);
    }

    private void UpdateSelectionButtons()
    {
        if (_isBusy)
        {
            _btnExtractSelected.Enabled = false;
            _btnReimportSelected.Enabled = false;
            return;
        }

        _btnExtractSelected.Enabled = _rootFolder is not null && HasExtractSelection();
        _btnReimportSelected.Enabled = ReinsertionSupported &&
                                      (GetSingleSelectedAssetForReimport() is not null ||
                                       GetSingleSelectedGroupForReimport() is not null);
    }

    private bool HasExtractSelection()
    {
        return _selectedTreeNodes.Any(node => node.TreeView == _tree && IsExtractableNode(node));
    }

    private ModelAsset? GetSingleSelectedAssetForReimport()
    {
        var selected = _selectedTreeNodes
            .Where(node => node.TreeView == _tree && IsExtractableNode(node))
            .ToList();
        return selected.Count == 1 && selected[0].Tag is ModelAsset asset
            ? asset
            : null;
    }

    private ModelAssetGroup? GetSingleSelectedGroupForReimport()
    {
        var selected = _selectedTreeNodes
            .Where(node => node.TreeView == _tree && IsExtractableNode(node))
            .ToList();
        return selected.Count == 1 && selected[0].Tag is ModelAssetGroup group
            ? group
            : null;
    }

    private (List<ModelAssetGroup> Groups, List<ModelAsset> Assets) GetSelectedItemsForExtraction()
    {
        var selected = _selectedTreeNodes
            .Where(node => node.TreeView == _tree && IsExtractableNode(node))
            .ToList();
        var groups = selected
            .Select(node => node.Tag)
            .OfType<ModelAssetGroup>()
            .Distinct()
            .ToList();
        var groupedMeshPaths = groups
            .SelectMany(group => group.Assets)
            .Select(asset => asset.MeshPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = selected
            .Select(node => node.Tag)
            .OfType<ModelAsset>()
            .Where(asset => !groupedMeshPaths.Contains(asset.MeshPath))
            .DistinctBy(asset => asset.MeshPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (groups, assets);
    }

    private void PreviewAsset(ModelAsset asset)
    {
        // A different model invalidates the playing animation's skeleton mapping.
        if (_animationPlayer is { Visible: true })
        {
            _animationPlayer.ClosePanel();
        }

        try
        {
            var mesh = D3DMeshParser.ParseFile(asset.MeshPath);
            SkeletonData? skeleton = null;
            if (asset.SkeletonPath is not null)
            {
                // A skeleton that fails to parse must not block the mesh+texture preview: some assets
                // (e.g. the Tales from the Borderlands source-leak characters/props) ship .skl files in a
                // layout we cannot read yet. Fall back to a skeleton-less preview instead of failing.
                try
                {
                    skeleton = SkeletonLoader.Load(asset.SkeletonPath, mesh.Version);
                }
                catch
                {
                    skeleton = null;
                }
            }

            var textures = _rootFolder is null
                ? new Dictionary<int, MaterialTextureSet>()
                : TextureResolver.ResolveForMesh(_rootFolder, asset.MeshPath, mesh);
            _preview.SetScene(mesh, skeleton, textures, partCount: 1);
            SetStatusText(Loc.T("status.preview_ready"));
            SetDetailText("");
        }
        catch (Exception ex)
        {
            _preview.SetScene(null, null);
            SetStatusText(Loc.T("status.preview_error", Path.GetFileName(asset.MeshPath)));
            SetDetailText(ex.Message);
        }
    }

    private void PreviewAssetGroup(ModelAssetGroup group)
    {
        // A different model invalidates the playing animation's skeleton mapping.
        if (_animationPlayer is { Visible: true })
        {
            _animationPlayer.ClosePanel();
        }

        try
        {
            if (_rootFolder is null)
            {
                return;
            }

            var previewAsset = ExtractionService.BuildPreviewAsset(group, _rootFolder);
            _preview.SetScene(previewAsset.Mesh, previewAsset.Skeleton, previewAsset.Textures, partCount: group.Assets.Count);
            SetStatusText(Loc.T("status.combined_preview_ready"));
            SetDetailText("");
        }
        catch (Exception ex)
        {
            _preview.SetScene(null, null);
            SetStatusText(Loc.T("status.preview_error", group));
            SetDetailText(ex.Message);
        }
    }

    // Selection context for animation discovery: the animations reference the rig, not the mesh
    // part, so matching uses the skeleton stem, the model name, and their digit-trimmed bases
    // (e.g. skM1_jesse201 → skM1_jesse).
    private (string ModelName, string SkeletonPath, HashSet<string> SearchTerms)? GetAnimationSearchContext()
    {
        var group = _selectedGroup;
        var asset = _selectedAsset;
        if (group is null && asset is null)
        {
            return null;
        }

        var skeletonPath = group?.SkeletonPath ?? asset?.SkeletonPath;
        if (string.IsNullOrWhiteSpace(skeletonPath))
        {
            return null;
        }

        var modelName = group?.Name ?? Path.GetFileNameWithoutExtension(asset!.MeshPath);
        var searchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileNameWithoutExtension(skeletonPath),
            modelName,
        };
        if (asset is not null)
        {
            searchTerms.Add(Path.GetFileNameWithoutExtension(asset.MeshPath));
        }
        foreach (var term in searchTerms.ToList())
        {
            var baseStem = term.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');
            if (baseStem.Length > 2 && baseStem.Length < term.Length)
            {
                searchTerms.Add(baseStem);
            }
        }

        return (modelName, skeletonPath, searchTerms);
    }

    // Discovers .anm candidates for the current selection (busy cursor while scanning); returns
    // null after showing the appropriate message when there is nothing to offer.
    private async Task<List<Export.AnimationCollector.Candidate>?> DiscoverAnimationCandidatesAsync(
        HashSet<string> searchTerms)
    {
        var root = _rootFolder!;
        SetBusy(true);
        List<Export.AnimationCollector.Candidate> candidates;
        try
        {
            candidates = await Task.Run(() =>
            {
                var strict = Export.AnimationCollector.FindCandidates(root, searchTerms.ToList());
                if (strict.Count > 0)
                {
                    return strict;
                }

                // Protagonist rigs are named differently from their meshes (JesseMale meshes animate
                // through skM1_jesse201_* rigs), so a strict full-name match finds nothing. Retry with
                // the distinctive name tokens (camelCase/underscore split), e.g. "Jesse".
                var looseTerms = BuildLooseAnimationTokens(searchTerms);
                return looseTerms.Count > 0
                    ? Export.AnimationCollector.FindCandidates(root, looseTerms)
                    : strict;
            });
        }
        finally
        {
            SetBusy(false);
        }

        if (candidates.Count == 0)
        {
            MessageBox.Show(this, Loc.T("anim.none_found", string.Join(", ", searchTerms)), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return candidates;
    }

    // Splits the strict search terms into name tokens and keeps only the distinctive ones:
    // "JesseMale_mouthAA" → Jesse (Male/mouth/AA are generic or too short to be identifying).
    private static List<string> BuildLooseAnimationTokens(IEnumerable<string> searchTerms)
    {
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "male", "female", "mouth", "default", "combined", "head", "body", "hair",
            "arm", "arms", "leg", "legs", "hand", "hands", "none", "eyes", "brows",
        };

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in searchTerms)
        {
            foreach (var piece in term.Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                // Split camelCase: a new token starts at each lower→Upper boundary.
                var start = 0;
                for (var i = 1; i <= piece.Length; i++)
                {
                    if (i == piece.Length || (char.IsUpper(piece[i]) && char.IsLower(piece[i - 1])))
                    {
                        var token = piece[start..i];
                        start = i;
                        // Distinctive = purely alphabetic and reasonably long.
                        if (token.Length >= 4 && token.All(char.IsLetter) && !generic.Contains(token))
                        {
                            tokens.Add(token);
                        }
                    }
                }
            }
        }

        return tokens.ToList();
    }

    // Opens the in-viewer animation player (View > Animations) for the current selection.
    private async Task ShowAnimationPlayerAsync()
    {
        if (_rootFolder is null)
        {
            return;
        }

        var context = GetAnimationSearchContext();
        if (context is null)
        {
            MessageBox.Show(this, Loc.T("anim.need_skinned"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var candidates = await DiscoverAnimationCandidatesAsync(context.Value.SearchTerms);
        if (candidates is null)
        {
            return;
        }

        if (_animationPlayer is null)
        {
            _animationPlayer = new AnimationPlayerPanel(_preview);
            _split.Panel2.Controls.Add(_animationPlayer);
            _preview.BringToFront();
        }

        _animationPlayer.Open(candidates);
    }

    // Exports the selected model (group or single skinned mesh) to GLB with the .anm animations
    // the user picks from the opened folder. Discovery is name-based; decoding + CRC64 bone
    // remapping happen during export, against the same skeleton the GLB carries.
    private async Task ExtractWithAnimationsAsync()
    {
        if (_rootFolder is null)
        {
            return;
        }

        var context = GetAnimationSearchContext();
        if (context is null)
        {
            MessageBox.Show(this, Loc.T("anim.need_skinned"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var group = _selectedGroup;
        var asset = _selectedAsset;
        var modelName = context.Value.ModelName;

        var root = _rootFolder;
        var candidates = await DiscoverAnimationCandidatesAsync(context.Value.SearchTerms);
        if (candidates is null)
        {
            return;
        }

        List<Export.AnimationCollector.Candidate> chosen;
        using (var dialog = new AnimationPickerDialog(modelName, candidates))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            chosen = dialog.SelectedCandidates;
        }

        if (chosen.Count == 0)
        {
            return;
        }

        var output = ChooseOutputFolder();
        if (output is null)
        {
            return;
        }

        await RunWithUiLockAsync(() => Task.Run(() =>
        {
            var decoded = Export.AnimationCollector.Decode(chosen);
            var path = group is not null
                ? ExtractionService.ExtractAssetGroupToPath(
                    group, root, Path.Combine(output, group.OutputStem + ".glb"), ExportFormat.Glb, decoded)
                : ExtractionService.ExtractAssetToPath(
                    asset!, root, Path.Combine(output, Path.GetFileNameWithoutExtension(asset!.MeshPath) + ".glb"),
                    ExportFormat.Glb, decoded);
            return Loc.T("anim.export_done", Path.GetFileName(path), decoded.Count, chosen.Count);
        }));
    }

    private async Task ExtractSelectedAsync()
    {
        if (_rootFolder is null)
        {
            return;
        }

        var selected = GetSelectedItemsForExtraction();
        var selectedCount = selected.Groups.Count + selected.Assets.Count;
        if (selectedCount == 0)
        {
            return;
        }

        var output = ChooseOutputFolder();
        if (output is null)
        {
            return;
        }

        var root = _rootFolder;
        var format = _exportFormat;
        var progress = new Progress<(int Done, string Name)>(p =>
            SetProgress(p.Done, selectedCount, Loc.T("status.extracting_item", p.Done, selectedCount, p.Name)));
        await RunWithUiLockAsync(async () =>
        {
            var result = await Task.Run(() =>
            {
                var usedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lines = new List<string>();
                var ok = 0;
                var failed = 0;
                var done = 0;

                foreach (var group in selected.Groups)
                {
                    ((IProgress<(int, string)>)progress).Report((++done, group.Name));
                    var outputPath = BuildSelectedExtractionOutputPath(output, group.OutputStem, format, usedOutputPaths);
                    try
                    {
                        ExtractionService.ExtractAssetGroupToPath(group, root, outputPath, format);
                        ok++;
                        lines.Add(Loc.T("report.ok_combined_group", group.Name, Path.GetFileName(outputPath)));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lines.Add(Loc.T("report.fail_combined_group", group.Name, ex.Message));
                    }
                }

                foreach (var asset in selected.Assets)
                {
                    var stem = Path.GetFileNameWithoutExtension(asset.MeshPath);
                    ((IProgress<(int, string)>)progress).Report((++done, stem));
                    var outputPath = BuildSelectedExtractionOutputPath(output, stem, format, usedOutputPaths);
                    try
                    {
                        ExtractionService.ExtractAssetToPath(asset, root, outputPath, format);
                        ok++;
                        lines.Add(Loc.T("report.ok_model", stem, Path.GetFileName(outputPath)));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lines.Add(Loc.T("report.fail_model", stem, ex.Message));
                    }
                }

                if (selectedCount == 1 && ok == 1 && failed == 0)
                {
                    return string.Join(Environment.NewLine, lines);
                }

                return string.Join(
                    Environment.NewLine,
                    Loc.T("report.extracted_selected_to", output),
                    Loc.T("report.ok_failed_count", ok, selectedCount, failed),
                    string.Join(Environment.NewLine, lines));
            });

            return result;
        });
    }

    private static string BuildSelectedExtractionOutputPath(
        string outputFolder,
        string stem,
        ExportFormat format,
        HashSet<string> usedOutputPaths)
    {
        var extension = format == ExportFormat.Glb ? ".glb" : ".gltf";
        var candidate = Path.Combine(outputFolder, stem + extension);
        var suffix = 2;
        while (!usedOutputPaths.Add(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(outputFolder, $"{stem}_{suffix++}{extension}");
        }

        return candidate;
    }

    private async Task ExtractAllAsync()
    {
        if (_rootFolder is null)
        {
            return;
        }

        var output = ChooseOutputFolder();
        if (output is null)
        {
            return;
        }

        var assets = _assets;
        var root = _rootFolder;
        var format = _exportFormat;
        await RunWithUiLockAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var done = 0;
            var total = assets.Count;
            var progress = new Progress<string>(line =>
            {
                SetStatusText(line);
                SetProgress(++done, total, Loc.T("status.extracting_count", Math.Min(done, total), total));
            });
            var summary = await Task.Run(() => ExtractionService.ExtractAll(assets, root, output, format, progress));
            sw.Stop();
            return Loc.T("report.time_suffix", summary, sw.Elapsed);
        });
    }

    private async Task ReimportSelectedAsync()
    {
        if (!ReinsertionSupported)
        {
            MessageBox.Show(
                GetReinsertionUnavailableMessage(),
                Loc.T("msg.reinsert.unavailable_title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var asset = GetSingleSelectedAssetForReimport();
        var group = GetSingleSelectedGroupForReimport();
        if (asset is not null)
        {
            await ReimportAssetAsync(asset);
        }
        else if (group is not null)
        {
            await ReimportAssetGroupAsync(group);
        }
    }

    private async Task ReimportAssetAsync(ModelAsset asset)
    {
        var input = ChooseReimportInputFile(Path.GetDirectoryName(asset.MeshPath));
        if (input is null)
        {
            return;
        }

        var outputFolder = ChooseReimportOutputFolder(Path.GetDirectoryName(asset.MeshPath) ?? "");
        if (outputFolder is null)
        {
            return;
        }
        var output = Path.Combine(outputFolder, Path.GetFileName(asset.MeshPath));

        await RunWithUiLockAsync(async () =>
        {
            var useDiffuseAtlas = _btnDiffuseAtlas.Checked;
            var uncompressedTextures = _uncompressedTextures;
            var matchOriginalSize = _matchOriginalModelSize;
            var normalizeFacialBones = _normalizeFacialBonesOnReimport;
            var result = await Task.Run(() => ReimportSingleAsset(asset, input, output, useDiffuseAtlas, uncompressedTextures, matchOriginalSize, normalizeFacialBones));
            return result;
        });

        if (Path.GetFullPath(output).Equals(Path.GetFullPath(asset.MeshPath), StringComparison.OrdinalIgnoreCase))
        {
            PreviewAsset(asset);
        }
    }

    private async Task ReimportAssetGroupAsync(ModelAssetGroup group)
    {
        if (_rootFolder is null)
        {
            return;
        }

        var input = ChooseReimportInputFile(Path.GetDirectoryName(group.Assets[0].MeshPath));
        if (input is null)
        {
            return;
        }

        var outputFolder = ChooseReimportOutputFolder(Path.GetDirectoryName(group.Assets[0].MeshPath) ?? "");
        if (outputFolder is null)
        {
            return;
        }

        var root = _rootFolder;
        var useDiffuseAtlas = _btnDiffuseAtlas.Checked;
        var uncompressedTextures = _uncompressedTextures;
        var matchOriginalSize = _matchOriginalModelSize;
        var normalizeFacialBones = _normalizeFacialBonesOnReimport;

        await RunWithUiLockAsync(async () =>
        {
            var result = await Task.Run(() =>
            {
                var model = GltfReader.Load(input);
                return ReimportCombinedGroup(group, root, model, input, outputFolder, useDiffuseAtlas, uncompressedTextures, matchOriginalSize, normalizeFacialBones);
            });

            return result;
        });

        if (group.Assets.Any(asset => Path.GetFullPath(outputFolder).Equals(Path.GetDirectoryName(Path.GetFullPath(asset.MeshPath)), StringComparison.OrdinalIgnoreCase)))
        {
            PreviewAssetGroup(group);
        }
    }

    private static string ReimportSingleAsset(
        ModelAsset asset,
        string input,
        string output,
        bool useDiffuseAtlas,
        bool uncompressedTextures,
        bool matchOriginalSize = false,
        bool normalizeFacialBonesOnReimport = false)
    {
        var gameConfig = GameConfig.Current.WithNormalizeFacialBonesOnReimport(normalizeFacialBonesOnReimport);
        var templateBytes = File.ReadAllBytes(asset.MeshPath);
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(input), gameConfig);
        if (matchOriginalSize)
        {
            MatchImportedModelToTemplateSize(model, templateBytes);
        }
        var effectiveUseDiffuseAtlas = useDiffuseAtlas;
        // With the atlas active, the line/normal textures must be present as image bytes to be packed, so
        // reload them from the template folder before atlasing (Blender strips them). Off the atlas path
        // the line and normal map are rebound by name in ReinsertTextureService, reusing the game's files.
        if (effectiveUseDiffuseAtlas)
        {
            model = StrippedLineTextureRecovery.RestoreStrippedTextures(model, asset.MeshPath);
        }
        // With the atlas active, line textures are baked into the atlas and their detail slots are dropped,
        // so normal detail-write inversion cannot run. Apply game-specific line alpha fixes before packing.
        if (effectiveUseDiffuseAtlas &&
            (gameConfig.InvertHeadLineAlphaOnReimport || gameConfig.InvertBodyLineAlphaOnReimport || gameConfig.InvertHandLineAlphaOnReimport))
        {
            model = CharacterLineAtlasFix.InvertCharacterLineAlpha(model, gameConfig);
        }
        var atlas = ApplyDiffuseAtlasIfRequested(model, effectiveUseDiffuseAtlas, asset.MeshPath);
        model = atlas.Model;

        if (BttfMeshSupport.IsBackToTheFutureMesh(templateBytes))
        {
            return ReimportBttfSingleAsset(asset, model, input, output, effectiveUseDiffuseAtlas, uncompressedTextures, gameConfig, atlas);
        }

        if (D3DMeshParser.Parse(templateBytes).Version == 25)
        {
            var v25Tex = ReinsertTextureService.WriteV25ReferencedTextures(model, asset.MeshPath, output, uncompressedTextures);
            var v25Layout = D3DMeshLayout.BuildV25(templateBytes);
            var v25SourceLayout = MeshReinserter.TryFindV25SourceMaterialLayout(model, asset.MeshPath, input);
            var v25Bytes = MeshReinserter.ReinsertV25Geometry(v25Layout, model, v25Tex.PrimitiveSlots, v25SourceLayout);
            File.WriteAllBytes(output, v25Bytes);
            var v25TextureLine = v25Tex.Written.Count > 0
                ? Loc.T("report.texture_line", v25Tex.Written.Count)
                : "";
            var v25WarnLine = v25Tex.TemplateNotFound.Count > 0
                ? Loc.T("report.v25.no_template_warning", string.Join(", ", v25Tex.TemplateNotFound))
                : "";
            var v25DistinctTex = ReinsertTextureService.DistinctV25TextureCount(model);
            var v25CollapseLine = v25DistinctTex > v25Layout.Materials.Count && !MeshReinserter.CanAddV25Materials(v25Layout)
                ? Loc.T("report.v25.collapse_warning", v25DistinctTex, v25Layout.Materials.Count)
                : "";
            // Skinned (character) V25: rebuild the .skl from the GLB skeleton next to the output, same as
            // the other games. No-op for static props (the model has no skin).
            var v25SkeletonPath = ResolveReimportSkeletonPath(asset);
            var v25SkeletonLine = v25Layout.IsSkinned
                ? RebuildSkeletonForReimport(asset, v25SkeletonPath, model, output, 25, gameConfig)
                : "";
            var v25Kind = v25Layout.IsSkinned ? Loc.T("report.v25.kind_character") : Loc.T("report.v25.kind_static");
            return Loc.T("report.v25.reimported", v25Kind, Path.GetFileName(asset.MeshPath), input, output, v25TextureLine, v25WarnLine, v25CollapseLine, v25SkeletonLine);
        }

        if (D3DMeshParser.Parse(templateBytes).Version == 45)
        {
            // MCSM Season 2: same user flow as the other games. The v45 writer distributes the GLB
            // primitives over the template batches (semantic diffuse pairing) and reports which
            // image each template diffuse name should carry — textures are then written from that
            // SAME mapping, so geometry and textures can never disagree.
            var (v45TemplateDiffuse, v45MissingProps) = ResolveV45TemplateDiffuse(asset.MeshPath);
            var v45Result = MeshReinserter.ReinsertV45GeometryWithAssignments(templateBytes, model, v45TemplateDiffuse);
            File.WriteAllBytes(output, v45Result.MeshBytes);
            var v45Written = ReinsertTextureService.WriteV45AssignedTextures(v45Result.Textures, asset.MeshPath, output, uncompressedTextures);
            var v45TextureLine = v45Written.Count > 0
                ? Loc.T("report.texture_line", v45Written.Count)
                : "";
            if (v45MissingProps)
            {
                v45TextureLine += Loc.T("report.v45.no_prop_warning");
            }
            var v45Kind = model.Primitives.Any(p => p.IsSkinned)
                ? Loc.T("report.v45.kind_character")
                : Loc.T("report.v45.kind_static");
            return Loc.T("report.v45.reimported", v45Kind, Path.GetFileName(asset.MeshPath), input, output, v45TextureLine);
        }

        var layout = D3DMeshLayout.Build(templateBytes);
        var skeletonPath = ResolveReimportSkeletonPath(asset);
        var skeleton = LoadSkeletonOrNull(skeletonPath, layout.Version);
        var textureOptions = BuildReinsertTextureOptions(effectiveUseDiffuseAtlas, uncompressedTextures);
        var textures = ReinsertTextureService.WriteAllReferencedTextures(model, asset.MeshPath, output, gameConfig, textureOptions);
        var bytes = MeshReinserter.ReinsertGeometry(layout, model, textures, skeleton, gameConfig);
        File.WriteAllBytes(output, bytes);

        var check = D3DMeshLayout.Build(bytes);
        var status = check.TailOffset + check.TailLength == bytes.Length
            ? Loc.T("report.status_verified")
            : Loc.T("report.status_eof_warning");
        var textureCount = textures.WrittenNames.Count;
        var textureLine = textureCount > 0
            ? Loc.T("report.texture_line", textureCount)
            : "";
        var skeletonLine = RebuildSkeletonForReimport(asset, skeletonPath, model, output, layout.Version, gameConfig);
        var atlasLine = BuildAtlasStatusLine(atlas);

        return Loc.T("report.reimported", Path.GetFileName(asset.MeshPath), input, output, textureLine, atlasLine, skeletonLine, status);
    }

    // Rescales the imported model so its overall size matches the template mesh it replaces (Settings >
    // "Match original model size"). The original bounds come from the template's own geometry, so this
    // works for every supported game/version. Failures are non-fatal: a model that cannot be measured is
    // simply left at its imported scale.
    private static void MatchImportedModelToTemplateSize(GltfModel model, byte[] templateBytes)
    {
        try
        {
            var bounds = D3DMeshParser.Parse(templateBytes).GetBounds();
            GltfModelScaler.MatchBounds(model, bounds);
        }
        catch
        {
            // Leave the imported model untouched if the template cannot be parsed for bounds.
        }
    }

    // Union of every group asset's original geometry bounds, used to scale a combined import as a whole.
    private static bool TryGetCombinedTemplateBounds(ModelAssetGroup group, out (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) bounds)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;
        var any = false;
        foreach (var asset in group.Assets)
        {
            try
            {
                var (bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ) = D3DMeshParser.ParseFile(asset.MeshPath).GetBounds();
                if (bMinX == 0 && bMinY == 0 && bMinZ == 0 && bMaxX == 0 && bMaxY == 0 && bMaxZ == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, bMinX);
                minY = Math.Min(minY, bMinY);
                minZ = Math.Min(minZ, bMinZ);
                maxX = Math.Max(maxX, bMaxX);
                maxY = Math.Max(maxY, bMaxY);
                maxZ = Math.Max(maxZ, bMaxZ);
                any = true;
            }
            catch
            {
                // Skip assets that cannot be parsed; the remaining ones still define the union.
            }
        }

        bounds = (minX, minY, minZ, maxX, maxY, maxZ);
        return any;
    }

    // Back to the Future (.d3dmesh v1, "ERTM") reimport. The V13/17 pipeline cannot read v1, so geometry
    // is rewritten by BttfMeshReinserter while textures reuse the shared ReinsertTextureService (its ERTM
    // .d3dtx path is already in place). Phase 1 handles static meshes; skinned v1 meshes are rejected.
    private static string ReimportBttfSingleAsset(
        ModelAsset asset,
        GltfModel model,
        string input,
        string output,
        bool effectiveUseDiffuseAtlas,
        bool uncompressedTextures,
        GameConfig gameConfig,
        GltfDiffuseAtlasResult atlas)
    {
        var templateBytes = File.ReadAllBytes(asset.MeshPath);
        // Back to the Future binds textures by name and we keep the template's references, so write the
        // imported model's textures under the template submeshes' own slot names, aligned by part. This
        // replaces the V13/17 ReinsertTextureService path (which renames by semantics and would mismatch
        // a v1 character swap).
        var textureCount = BttfMeshSupport.WriteAlignedTextures(asset.MeshPath, output, model, uncompressedTextures);
        var bytes = BttfMeshSupport.ReinsertGeometry(templateBytes, model, model.Skeleton);

        // Keep the prop's baked lightmap only when the imported model supplies its own bake (e.g. a model
        // extracted from the game). For an unrelated/external model the inherited bake would multiply it to
        // black in-game, so its reference is removed from the mesh (no bake shipped).
        var removedBakeRefs = 0;
        if (gameConfig.ClearInheritedBakeOnReimport && !BttfMeshSupport.ModelDeclaresBake(model))
        {
            (bytes, removedBakeRefs) = BttfMeshSupport.BreakInheritedBakeReference(bytes, asset.MeshPath);
        }

        File.WriteAllBytes(output, bytes);

        var status = BttfMeshSupport.VerifyClosesAtEof(bytes)
            ? Loc.T("report.status_verified")
            : Loc.T("report.status_eof_warning");
        var textureLine = textureCount > 0
            ? Loc.T("report.texture_line", textureCount)
            : "";
        var bakeLine = removedBakeRefs > 0
            ? Loc.T("report.bttf.bake_removed")
            : "";
        var atlasLine = BuildAtlasStatusLine(atlas);
        var skeletonLine = RebuildBttfSkeletonForReimport(asset, model, output);

        return Loc.T("report.bttf.reimported", Path.GetFileName(asset.MeshPath), input, output, textureLine, bakeLine, atlasLine, skeletonLine, status);
    }

    // Writes the .skl next to the reimported Back to the Future mesh, rebuilt from the GLB's own skeleton
    // (character swaps need the imported model's bones, not the target's). The ERTM header is reused from
    // the target's .skl. No skin in the GLB, or no target .skl, leaves the original skeleton in place.
    private static string RebuildBttfSkeletonForReimport(ModelAsset asset, GltfModel model, string output)
    {
        if (model.Skeleton is null || model.Skeleton.Bones.Count == 0)
        {
            return "";
        }

        if (asset.SkeletonPath is null || !File.Exists(asset.SkeletonPath))
        {
            return Loc.T("report.skeleton.bttf_skipped_no_skl");
        }

        try
        {
            var skeletonBytes = BttfSkeletonWriter.Build(File.ReadAllBytes(asset.SkeletonPath), model.Skeleton);
            var skeletonOutput = Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileName(asset.SkeletonPath));
            File.WriteAllBytes(skeletonOutput, skeletonBytes);
            return Loc.T("report.skeleton.bttf_rebuilt", Path.GetFileName(asset.SkeletonPath), model.Skeleton.Bones.Count);
        }
        catch (Exception ex)
        {
            return Loc.T("report.skeleton.bttf_rebuild_failed", ex.Message);
        }
    }

    // Rebuilds the .skl next to the reimported mesh from the skeleton inside the GLB. When the model
    // keeps the game's original skeleton, the rebuild merges the edits onto the original (so an
    // untouched skeleton stays byte-identical). Prop targets intentionally stay geometry-only: a
    // skinned GLB can be used as a static prop, but the target should not gain a brand-new .skl.
    // Returns a status line (empty when the GLB carries no skin or the target has no skeleton).
    private static string RebuildSkeletonForReimport(ModelAsset asset, string? skeletonPath, GltfModel model, string output, int skeletonVersion, GameConfig gameConfig)
    {
        if (model.Skeleton is null || model.Skeleton.Bones.Count == 0)
        {
            if (skeletonPath is not null && File.Exists(skeletonPath))
            {
                return Loc.T("report.skeleton.static_not_written");
            }

            return "";
        }

        if (skeletonPath is null || !File.Exists(skeletonPath))
        {
            return Loc.T("report.skeleton.skipped_no_skl_static");
        }

        try
        {
            var outputDir = Path.GetDirectoryName(output) ?? "";
            var skeletonName = Path.GetFileName(skeletonPath);
            var skeletonOutput = Path.Combine(outputDir, skeletonName);

            if (TryNormalizeAxisConvertedSkeletonForReimport(
                    skeletonPath,
                    skeletonVersion,
                    model.Skeleton,
                    out _,
                    out var normalizedSkeleton,
                    out var keptStatus))
            {
                var normalizedSkeletonBytes = RebuildSkeletonBytesForGame(skeletonPath, normalizedSkeleton, gameConfig);
                File.WriteAllBytes(skeletonOutput, normalizedSkeletonBytes);
                return "\n" + keptStatus;
            }

            var skeletonBytes = RebuildSkeletonBytesForGame(skeletonPath, model.Skeleton, gameConfig);
            File.WriteAllBytes(skeletonOutput, skeletonBytes);
            return Loc.T("report.skeleton.rebuilt_edits", skeletonName, model.Skeleton.Bones.Count);
        }
        catch (Exception ex)
        {
            if (gameConfig.IsOriginalTalesFromTheBorderlandsPc)
            {
                throw new InvalidDataException($"Could not rebuild the TFTB original PC .skl for '{Path.GetFileName(asset.MeshPath)}'.", ex);
            }

            return Loc.T("report.skeleton.rebuild_failed", ex.Message);
        }
    }

    private static byte[] RebuildSkeletonBytesForGame(string skeletonPath, SkeletonData skeleton, GameConfig gameConfig)
        => SkeletonRebuilder.RebuildWithEdits(skeletonPath, skeleton, gameConfig);

    private static string? ResolveReimportSkeletonPath(ModelAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.SkeletonPath) && File.Exists(asset.SkeletonPath))
        {
            return asset.SkeletonPath;
        }

        var sameStem = Path.ChangeExtension(asset.MeshPath, ".skl");
        if (File.Exists(sameStem))
        {
            return sameStem;
        }

        var folder = Path.GetDirectoryName(asset.MeshPath);
        return string.IsNullOrWhiteSpace(folder)
            ? null
            : SkeletonResolver.FindForMesh(folder, asset.MeshPath);
    }

    internal static string ReimportCombinedGroup(
        ModelAssetGroup group,
        string inputRoot,
        GltfModel combinedModel,
        string input,
        string outputFolder,
        bool useDiffuseAtlas,
        bool uncompressedTextures,
        bool matchOriginalSize = false,
        bool normalizeFacialBonesOnReimport = false)
    {
        Directory.CreateDirectory(outputFolder);
        var gameConfig = GameConfig.Current.WithNormalizeFacialBonesOnReimport(normalizeFacialBonesOnReimport);
        combinedModel = GltfModelPreprocessor.ApplyGameReinsertRules(
            combinedModel,
            gameConfig,
            preserveEyeHelperPrimitives: ShouldPreserveCombinedEyeHelperPrimitives(combinedModel, gameConfig));
        if (matchOriginalSize && TryGetCombinedTemplateBounds(group, out var combinedBounds))
        {
            // Scale the whole combined import as one unit against the union of the group's original
            // meshes, so the assembly keeps its internal proportions and part alignment.
            GltfModelScaler.MatchBounds(combinedModel, combinedBounds);
        }
        var sourcePrimitives = BuildCombinedSourcePrimitiveMap(group, combinedModel, inputRoot, out var splitModeLine, out var externalSplit);
        var combinedSkeleton = BuildCombinedReferenceSkeletonForReimport(group, combinedModel, inputRoot, outputFolder, gameConfig, out var skeletonLine);
        var ok = 0;
        var skipped = 0;
        var invisible = 0;
        var totalTextures = 0;
        var lines = new List<string>();

        foreach (var asset in group.Assets)
        {
            var fullMeshPath = Path.GetFullPath(asset.MeshPath);
            if (!sourcePrimitives.TryGetValue(fullMeshPath, out var primitives) || primitives.Count == 0)
            {
                var invisibleOutput = Path.Combine(outputFolder, Path.GetFileName(asset.MeshPath));
                var invisibleTemplateBytes = File.ReadAllBytes(asset.MeshPath);
                if (BttfMeshSupport.IsBackToTheFutureMesh(invisibleTemplateBytes))
                {
                    // Phase 1 keeps unassigned Back to the Future parts as the game's original mesh rather
                    // than synthesising a v1 no-triangle placeholder.
                    File.WriteAllBytes(invisibleOutput, invisibleTemplateBytes);
                    invisible++;
                    lines.Add(Loc.T("report.invisible.bttf_kept", Path.GetFileName(asset.MeshPath)));
                    continue;
                }

                if (D3DMeshParser.Parse(invisibleTemplateBytes).Version == 25)
                {
                    // Make the unassigned Michonne (V25) part invisible by zeroing its index buffer, same as
                    // the other games — keeping the original mesh would leave the receiver's old part showing
                    // through the replacement (e.g. the old hair over the new one).
                    var invisibleV25 = BuildInvisibleV25MeshBytes(asset.MeshPath);
                    File.WriteAllBytes(invisibleOutput, invisibleV25);
                    invisible++;
                    lines.Add(Loc.T("report.invisible.v25_placeholder", Path.GetFileName(asset.MeshPath)));
                    continue;
                }

                if (D3DMeshParser.Parse(invisibleTemplateBytes).Version == 45)
                {
                    // Unassigned MCSM Season 2 part: blank its triangles so the receiver's old part
                    // does not show through the replacement.
                    var invisibleV45 = MeshReinserter.BuildInvisibleV45MeshBytes(asset.MeshPath);
                    File.WriteAllBytes(invisibleOutput, invisibleV45);
                    invisible++;
                    lines.Add(Loc.T("report.invisible.v45_placeholder", Path.GetFileName(asset.MeshPath)));
                    continue;
                }

                var invisibleBytes = BuildInvisibleMeshBytes(asset.MeshPath);
                File.WriteAllBytes(invisibleOutput, invisibleBytes);

                var invisibleCheck = D3DMeshLayout.Build(invisibleBytes);
                var invisibleStatus = invisibleCheck.TailOffset + invisibleCheck.TailLength == invisibleBytes.Length ? Loc.T("report.token.verified") : Loc.T("report.token.layout_warning");
                invisible++;
                lines.Add(Loc.T("report.invisible.placeholder", Path.GetFileName(asset.MeshPath), invisibleStatus));
                continue;
            }

            primitives = primitives
                .OrderBy(primitive => primitive.SourceSubmeshIndex ?? int.MaxValue)
                .ThenBy(primitive => combinedModel.Primitives.IndexOf(primitive))
                .ToList();
            var partModel = new GltfModel
            {
                Primitives = primitives,
                Joints = combinedModel.Joints,
                Skeleton = combinedModel.Skeleton,
            };
            var autoDiffuseAtlas = ShouldAutoAtlasCombinedPart(gameConfig, useDiffuseAtlas, externalSplit, partModel, asset.MeshPath);
            var effectiveUseDiffuseAtlas = useDiffuseAtlas || autoDiffuseAtlas;
            if (effectiveUseDiffuseAtlas)
            {
                partModel = StrippedLineTextureRecovery.RestoreStrippedTextures(partModel, asset.MeshPath);
            }
            if (effectiveUseDiffuseAtlas &&
                (gameConfig.InvertHeadLineAlphaOnReimport || gameConfig.InvertBodyLineAlphaOnReimport || gameConfig.InvertHandLineAlphaOnReimport))
            {
                partModel = CharacterLineAtlasFix.InvertCharacterLineAlpha(partModel, gameConfig);
            }
            var atlas = ApplyDiffuseAtlasIfRequested(partModel, effectiveUseDiffuseAtlas, asset.MeshPath);
            partModel = atlas.Model;
            var output = Path.Combine(outputFolder, Path.GetFileName(asset.MeshPath));

            var partTemplateBytes = File.ReadAllBytes(asset.MeshPath);
            if (BttfMeshSupport.IsBackToTheFutureMesh(partTemplateBytes))
            {
                var bttfTextureCount = BttfMeshSupport.WriteAlignedTextures(asset.MeshPath, output, partModel, uncompressedTextures);
                var bttfBytes = BttfMeshSupport.ReinsertGeometry(partTemplateBytes, partModel, partModel.Skeleton);
                if (gameConfig.ClearInheritedBakeOnReimport && !BttfMeshSupport.ModelDeclaresBake(partModel))
                {
                    (bttfBytes, _) = BttfMeshSupport.BreakInheritedBakeReference(bttfBytes, asset.MeshPath);
                }

                File.WriteAllBytes(output, bttfBytes);
                RebuildBttfSkeletonForReimport(asset, partModel, output);
                totalTextures += bttfTextureCount;
                ok++;
                var bttfStatus = BttfMeshSupport.VerifyClosesAtEof(bttfBytes) ? Loc.T("report.token.verified") : Loc.T("report.token.layout_warning");
                lines.Add(Loc.T("report.ok_part_bttf", Path.GetFileName(asset.MeshPath), primitives.Count, bttfTextureCount, bttfStatus));
                continue;
            }

            if (D3DMeshParser.Parse(partTemplateBytes).Version == 25)
            {
                // Michonne (V25) part: reinsert via the dedicated V25 path. The combined .skl is rebuilt
                // once by BuildCombinedReferenceSkeletonForReimport, so it is not written per part here.
                // partModel.Joints is the combined skin's joints, so each part's blend indices resolve
                // against its own template palette (a subset of the shared skeleton).
                var v25Tex = ReinsertTextureService.WriteV25ReferencedTextures(partModel, asset.MeshPath, output, uncompressedTextures);
                var v25Layout = D3DMeshLayout.BuildV25(partTemplateBytes);
                var v25SourceLayout = MeshReinserter.TryFindV25SourceMaterialLayout(partModel, asset.MeshPath);
                var v25Bytes = MeshReinserter.ReinsertV25Geometry(v25Layout, partModel, v25Tex.PrimitiveSlots, v25SourceLayout);
                File.WriteAllBytes(output, v25Bytes);
                totalTextures += v25Tex.Written.Count;
                ok++;
                var v25Kind = v25Layout.IsSkinned ? Loc.T("report.v25.kind_character") : Loc.T("report.v25.kind_static");
                lines.Add(Loc.T("report.ok_part_v25", Path.GetFileName(asset.MeshPath), primitives.Count, v25Tex.Written.Count, v25Kind));
                continue;
            }

            if (D3DMeshParser.Parse(partTemplateBytes).Version == 45)
            {
                // MCSM Season 2 part: primitives are distributed over the template batches with
                // semantic diffuse pairing, and textures are written from that SAME mapping (each
                // template diffuse name gets its batch's image). The combined skin's joints resolve
                // per part against the template's own bone list by CRC64.
                var (v45TemplateDiffuse, v45MissingProps) = ResolveV45TemplateDiffuse(asset.MeshPath);
                var v45Result = MeshReinserter.ReinsertV45GeometryWithAssignments(partTemplateBytes, partModel, v45TemplateDiffuse);
                File.WriteAllBytes(output, v45Result.MeshBytes);
                var v45Written = ReinsertTextureService.WriteV45AssignedTextures(v45Result.Textures, asset.MeshPath, output, uncompressedTextures);
                totalTextures += v45Written.Count;
                ok++;
                var v45Kind = partModel.Primitives.Any(p => p.IsSkinned)
                    ? Loc.T("report.v45.kind_character")
                    : Loc.T("report.v45.kind_static");
                lines.Add(Loc.T("report.ok_part_v45", Path.GetFileName(asset.MeshPath), primitives.Count, v45Written.Count, v45Kind) +
                          (v45MissingProps ? Loc.T("report.v45.no_prop_warning") : ""));
                continue;
            }

            var layout = D3DMeshLayout.Build(partTemplateBytes);
            var skeleton = combinedSkeleton ?? LoadSkeletonOrNull(asset.SkeletonPath, layout.Version);
            var textureOptions = BuildReinsertTextureOptions(effectiveUseDiffuseAtlas, uncompressedTextures);
            var textures = ReinsertTextureService.WriteAllReferencedTextures(partModel, asset.MeshPath, output, gameConfig, textureOptions);
            var bytes = MeshReinserter.ReinsertGeometry(layout, partModel, textures, skeleton, gameConfig);
            File.WriteAllBytes(output, bytes);

            var check = D3DMeshLayout.Build(bytes);
            var status = check.TailOffset + check.TailLength == bytes.Length ? Loc.T("report.token.verified") : Loc.T("report.token.layout_warning");
            totalTextures += textures.WrittenNames.Count;
            ok++;
            var atlasSummary = atlas.Applied
                ? Loc.T("report.atlas_summary", atlas.SourceTextureCount, atlas.AtlasWidth, atlas.AtlasHeight)
                : "";
            if (autoDiffuseAtlas && atlas.Applied)
            {
                atlasSummary += Loc.T("report.atlas_auto_suffix");
            }
            lines.Add(Loc.T("report.ok_part", Path.GetFileName(asset.MeshPath), primitives.Count, textures.WrittenNames.Count, atlasSummary, status));
        }

        if (ok == 0)
        {
            throw new InvalidOperationException(
                "The selected GLB/GLTF could not be split for this Combined group. " +
                "Use a model with recognizable part/material names, or extract a Combined model with this tool and keep the primitive extras/source data.");
        }

        // Paths that actually received imported primitives. Group assets without any (written above
        // as invisible placeholders) are still companion-port candidates: a donor file with the same
        // part suffix can rebuild them (e.g. the group's mouthNone slot from the donor's mouthNone).
        var assignedPaths = sourcePrimitives
            .Where(static pair => pair.Value.Count > 0)
            .Select(static pair => Path.GetFullPath(pair.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var companionParts = gameConfig.PortCompanionVariantPartsOnReimport
            ? PortCompanionVariantParts(group, combinedModel, inputRoot, combinedSkeleton, outputFolder, gameConfig, assignedPaths, lines)
            : 0;

        return string.Join(
            Environment.NewLine,
            Loc.T("report.combined.header", group.Name),
            Loc.T("report.combined.input", input),
            Loc.T("report.combined.output_folder", outputFolder),
            splitModeLine,
            skeletonLine,
            Loc.T("report.combined.parts_summary", ok, group.Assets.Count, invisible, skipped, companionParts, totalTextures),
            string.Join(Environment.NewLine, lines));
    }

    // Ports the imported character's sibling part files (e.g. MCSM mouth visemes, which live next to
    // the GLB's source meshes but are never inside the combined GLB) into the target character's
    // matching files. Without this, the target's untouched viseme files keep their old UVs over the
    // replaced texture atlas and the mouth breaks whenever the character talks. Donor files are
    // located via each primitive's extras.sourceMesh path; targets are files in the group's folder
    // sharing the group's filename prefix that are not part of the combined group itself.
    private static int PortCompanionVariantParts(
        ModelAssetGroup group,
        GltfModel combinedModel,
        string inputRoot,
        SkeletonData? referenceSkeleton,
        string outputFolder,
        GameConfig gameConfig,
        IReadOnlySet<string> assignedPaths,
        List<string> lines)
    {
        if (referenceSkeleton is null || referenceSkeleton.Bones.Count == 0)
        {
            return 0;
        }

        // extras.sourceMesh is stored relative to the extraction root, same resolution as the
        // exact-path split in BuildCombinedSourcePrimitiveMap.
        var sourcePaths = combinedModel.Primitives
            .Select(static primitive => primitive.SourceMeshPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path!) ? path! : Path.Combine(inputRoot, path!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        if (sourcePaths.Count == 0)
        {
            return 0;
        }

        var sourceDir = Path.GetDirectoryName(sourcePaths[0]) ?? "";
        var sourcePrefix = LongestCommonFilenamePrefix(sourcePaths.Select(Path.GetFileNameWithoutExtension)!);
        var targetPrefix = LongestCommonFilenamePrefix(group.Assets.Select(static asset => Path.GetFileNameWithoutExtension(asset.MeshPath)));
        var targetDir = Path.GetDirectoryName(group.Assets[0].MeshPath) ?? "";
        if (sourcePrefix.Length == 0 || targetPrefix.Length == 0 || targetDir.Length == 0)
        {
            return 0;
        }

        var ported = 0;
        foreach (var candidate in Directory.EnumerateFiles(targetDir, targetPrefix + "*.d3dmesh"))
        {
            var fullCandidate = Path.GetFullPath(candidate);
            if (assignedPaths.Contains(fullCandidate))
            {
                continue;
            }

            var suffix = Path.GetFileNameWithoutExtension(candidate)[targetPrefix.Length..];
            if (suffix.Length == 0)
            {
                continue;
            }

            var donor = Path.Combine(sourceDir, sourcePrefix + suffix + ".d3dmesh");
            if (!File.Exists(donor) && suffix.StartsWith("mouth", StringComparison.OrdinalIgnoreCase))
            {
                // A viseme the donor character does not have still flips fast during lipsync; the
                // donor's neutral mouth is a sane stand-in, stale target UVs over the new atlas are not.
                donor = new[] { "mouthDefault", "mouthNone" }
                    .Select(neutral => Path.Combine(sourceDir, sourcePrefix + neutral + ".d3dmesh"))
                    .FirstOrDefault(File.Exists) ?? donor;
            }

            if (!File.Exists(donor))
            {
                continue;
            }

            var candidateName = Path.GetFileName(candidate);
            try
            {
                var candidateBytes = File.ReadAllBytes(candidate);
                if (D3DMeshParser.Parse(candidateBytes).Version == 25)
                {
                    var donorMeshV25 = D3DMeshParser.ParseFile(donor);
                    var primitivesV25 = DonorPartPrimitiveBuilder.Build(donorMeshV25, referenceSkeleton);
                    if (primitivesV25.Count == 0)
                    {
                        continue;
                    }

                    // Joints0 from the donor builder index into the reference skeleton, so expose that
                    // skeleton as the model's joint list for the V25 encoder to resolve against the palette.
                    var partModelV25 = new GltfModel
                    {
                        Primitives = primitivesV25,
                        Joints = referenceSkeleton.Bones.Select(b => new GltfJoint { Name = b.Name, Hash = b.Hash }).ToList(),
                        Skeleton = referenceSkeleton,
                    };
                    var outputV25 = Path.Combine(outputFolder, candidateName);
                    var texV25 = ReinsertTextureService.WriteV25ReferencedTextures(partModelV25, candidate, outputV25, forceUncompressed: false);
                    var layoutV25 = D3DMeshLayout.BuildV25(candidateBytes);
                    var sourceLayoutV25 = MeshReinserter.TryFindV25SourceMaterialLayout(partModelV25, candidate, donor);
                    var bytesV25 = MeshReinserter.ReinsertV25Geometry(layoutV25, partModelV25, texV25.PrimitiveSlots, sourceLayoutV25);
                    File.WriteAllBytes(outputV25, bytesV25);
                    ported++;
                    lines.Add(Loc.T("report.companion.ok_v25", candidateName, Path.GetFileName(donor)));
                    continue;
                }

                if (D3DMeshParser.Parse(candidateBytes).Version == 45)
                {
                    var donorMeshV45 = D3DMeshParser.ParseFile(donor);
                    var primitivesV45 = DonorPartPrimitiveBuilder.Build(donorMeshV45, referenceSkeleton);
                    if (primitivesV45.Count == 0)
                    {
                        continue;
                    }

                    var partModelV45 = new GltfModel
                    {
                        Primitives = primitivesV45,
                        Joints = referenceSkeleton.Bones.Select(b => new GltfJoint { Name = b.Name, Hash = b.Hash }).ToList(),
                        Skeleton = referenceSkeleton,
                    };
                    var outputV45 = Path.Combine(outputFolder, candidateName);
                    var bytesV45 = MeshReinserter.ReinsertV45Geometry(candidateBytes, partModelV45);
                    File.WriteAllBytes(outputV45, bytesV45);
                    ported++;
                    lines.Add(Loc.T("report.companion.ok_v45", candidateName, Path.GetFileName(donor)));
                    continue;
                }

                var layout = D3DMeshLayout.Build(candidateBytes);
                var donorMesh = D3DMeshParser.ParseFile(donor);
                var primitives = DonorPartPrimitiveBuilder.Build(donorMesh, referenceSkeleton);
                if (primitives.Count == 0)
                {
                    continue;
                }

                var partModel = new GltfModel { Primitives = primitives };
                var textureSlots = BuildTemplateTextureSlotsForCompanionPart(candidate, primitives.Count);
                var bytes = MeshReinserter.ReinsertGeometry(
                    layout,
                    partModel,
                    new ReinsertedTextures(textureSlots, []),
                    referenceSkeleton,
                    gameConfig);
                File.WriteAllBytes(Path.Combine(outputFolder, candidateName), bytes);
                ported++;
                lines.Add(Loc.T("report.companion.ok", candidateName, Path.GetFileName(donor)));
            }
            catch (Exception ex)
            {
                lines.Add(Loc.T("report.companion.warn", candidateName, ex.Message));
            }
        }

        return ported;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> BuildTemplateTextureSlotsForCompanionPart(string templateMeshPath, int primitiveCount)
    {
        var result = new List<IReadOnlyDictionary<string, string>>(primitiveCount);
        MeshData? templateMesh = null;
        try
        {
            templateMesh = D3DMeshParser.ParseFile(templateMeshPath);
        }
        catch
        {
            templateMesh = null;
        }

        for (var primitiveIndex = 0; primitiveIndex < primitiveCount; primitiveIndex++)
        {
            if (templateMesh is null || templateMesh.Submeshes.Count == 0)
            {
                result.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var templateSubmesh = templateMesh.Submeshes[Math.Min(primitiveIndex, templateMesh.Submeshes.Count - 1)];
            result.Add(templateSubmesh.TextureNames
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    private static byte[] BuildInvisibleMeshBytes(string templateMeshPath)
    {
        // Keep the template mesh STRUCTURALLY INTACT — same vertex buffers, submesh table, polygon counts,
        // bone palettes, texture groups and section offsets as the game's own (valid) file — and make it
        // invisible by collapsing every triangle to a degenerate one: zeroing the index buffer turns each
        // triangle into (v0, v0, v0), which has zero area and never rasterizes, in any pose. The earlier
        // approach (removing the face buffer + zeroing the global/submesh face counts) left a mesh the
        // Tales from the Borderlands 2014/2015 (v17) runtime rejected → crash on load. A complete mesh with
        // degenerate triangles loads exactly like a normal one, just draws nothing.
        var original = File.ReadAllBytes(templateMeshPath);
        var layout = D3DMeshLayout.Build(original);
        var result = (byte[])original.Clone();
        result.AsSpan(layout.FaceDataOffset, layout.FaceDataLength).Clear();
        return result;
    }

    // V25 (Michonne) equivalent of BuildInvisibleMeshBytes: keep the game's own mesh fully intact and make
    // it invisible by zeroing the index buffer so every triangle collapses to (0,0,0) — zero area, never
    // drawn, in any pose. Used for Combined parts the imported GLB doesn't provide, so the receiver's old
    // part (e.g. hair) doesn't show through the replacement instead of disappearing.
    private static byte[] BuildInvisibleV25MeshBytes(string templateMeshPath)
    {
        var original = File.ReadAllBytes(templateMeshPath);
        var layout = D3DMeshLayout.BuildV25(original);
        var result = (byte[])original.Clone();
        if (layout.FaceBuffer.PayloadLength > 0)
        {
            result.AsSpan(layout.FaceBuffer.PayloadOffset, layout.FaceBuffer.PayloadLength).Clear();
        }

        return result;
    }

    private static byte[] U32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)value));
        return bytes;
    }

    private static void WriteU32(byte[] bytes, int offset, int value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), unchecked((uint)value));

    // Locates the imported character's own .skl next to its source meshes (resolved from the GLB's
    // extras.sourceMesh paths), so the rebuild can adopt the donor's per-bone translation scales.
    private static SkeletonData? LoadDonorSkeletonForScales(GltfModel model, string inputRoot, GameConfig gameConfig)
    {
        if (!gameConfig.PortTranslationScalesOnSkeletonMerge)
        {
            return null;
        }

        var sourcePaths = model.Primitives
            .Select(static primitive => primitive.SourceMeshPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path!) ? path! : Path.Combine(inputRoot, path!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        if (sourcePaths.Count == 0)
        {
            return null;
        }

        var sourceDir = Path.GetDirectoryName(sourcePaths[0]) ?? "";
        var sourcePrefix = LongestCommonFilenamePrefix(sourcePaths.Select(static path => Path.GetFileNameWithoutExtension(path)!));
        var donorSkl = Path.Combine(sourceDir, sourcePrefix.TrimEnd('_') + ".skl");
        if (sourcePrefix.Length == 0 || !File.Exists(donorSkl))
        {
            return null;
        }

        try
        {
            return SkeletonRebuilder.ParseWithToolkit(donorSkl);
        }
        catch
        {
            return null;
        }
    }

    private static SkeletonData? BuildCombinedReferenceSkeletonForReimport(
        ModelAssetGroup group,
        GltfModel model,
        string inputRoot,
        string outputFolder,
        GameConfig gameConfig,
        out string statusLine)
    {
        statusLine = Loc.T("report.skeleton.skipped");
        var skeletonVersion = ResolveCombinedSkeletonVersion(group);

        if (string.IsNullOrWhiteSpace(group.SkeletonPath) || !File.Exists(group.SkeletonPath))
        {
            if (model.Skeleton is { Bones.Count: > 0 } foreignSkeleton)
            {
                var outputPath = Path.Combine(outputFolder, group.OutputStem + ".skl");
                var skeletonBytes = SkeletonRebuilder.WriteNewSkeleton(foreignSkeleton, gameConfig.DisplayName);
                File.WriteAllBytes(outputPath, skeletonBytes);
                statusLine = Loc.T("report.skeleton.created", Path.GetFileName(outputPath), foreignSkeleton.Bones.Count);
                return LoadSkeletonOrNull(outputPath, skeletonVersion);
            }

            statusLine = Loc.T("report.skeleton.skipped_no_skl");
            return null;
        }

        if (model.Skeleton is null || model.Skeleton.Bones.Count == 0)
        {
            var original = LoadSkeletonOrNull(group.SkeletonPath, skeletonVersion);
            var outputPath = Path.Combine(outputFolder, Path.GetFileName(group.SkeletonPath));
            if (!Path.GetFullPath(group.SkeletonPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(group.SkeletonPath, outputPath, overwrite: true);
            }

            statusLine = Loc.T("report.skeleton.kept", Path.GetFileName(group.SkeletonPath));
            return original;
        }

        try
        {
            var skeletonName = Path.GetFileName(group.SkeletonPath);
            var outputPath = Path.Combine(outputFolder, skeletonName);
            if (TryNormalizeAxisConvertedSkeletonForReimport(
                    group.SkeletonPath,
                    skeletonVersion,
                    model.Skeleton,
                    out var originalSkeleton,
                    out var normalizedSkeleton,
                    out statusLine))
            {
                var normalizedSkeletonBytes = RebuildSkeletonBytesForGame(group.SkeletonPath, normalizedSkeleton, gameConfig);
                File.WriteAllBytes(outputPath, normalizedSkeletonBytes);
                return LoadSkeletonOrNull(outputPath, skeletonVersion) ?? originalSkeleton;
            }

            var scaleDonor = LoadDonorSkeletonForScales(model, inputRoot, gameConfig);
            var skeletonBytes = gameConfig.Id == GameId.MinecraftStoryModeSeason2
                // v45 rigs store the pose twice (raw + RestXform); the delta rebuild keeps both in
                // sync with what the GLB actually carries.
                ? SkeletonRebuilder.RebuildV45WithEdits(group.SkeletonPath, model.Skeleton)
                : gameConfig.IsOriginalTalesFromTheBorderlandsPc || gameConfig.Id == GameId.GameOfThrones
                    ? RebuildSkeletonBytesForGame(group.SkeletonPath, model.Skeleton, gameConfig)
                    : SkeletonRebuilder.RebuildWithEdits(group.SkeletonPath, model.Skeleton, scaleDonor);
            File.WriteAllBytes(outputPath, skeletonBytes);
            var rebuiltSkeleton = LoadSkeletonOrNull(outputPath, skeletonVersion);
            var scalesNote = scaleDonor is not null ? Loc.T("report.skeleton.scales_note") : "";
            var boneCountLine = rebuiltSkeleton is null
                ? Loc.T("report.skeleton.bones_imported", model.Skeleton.Bones.Count)
                : Loc.T("report.skeleton.bones_final_imported", rebuiltSkeleton.Bones.Count, model.Skeleton.Bones.Count);
            statusLine = Loc.T("report.skeleton.rebuilt_combined", skeletonName, boneCountLine, scalesNote);
            return rebuiltSkeleton;
        }
        catch (Exception ex)
        {
            if (gameConfig.IsOriginalTalesFromTheBorderlandsPc)
            {
                throw new InvalidDataException($"Could not rebuild the TFTB original PC Combined .skl for '{group.Name}'.", ex);
            }

            statusLine = Loc.T("report.skeleton.rebuild_failed_combined", ex.Message);
            return LoadSkeletonOrNull(group.SkeletonPath, skeletonVersion);
        }
    }

    private static int ResolveCombinedSkeletonVersion(ModelAssetGroup group)
    {
        foreach (var asset in group.Assets)
        {
            try
            {
                // D3DMeshParser handles every supported version (incl. V25), unlike the V13/18-only
                // D3DMeshLayout which throws for Michonne.
                return D3DMeshParser.Parse(File.ReadAllBytes(asset.MeshPath)).Version;
            }
            catch
            {
                // Try the next part; if none parse, fall back to the legacy skeleton reader version.
            }
        }

        return 13;
    }

    private static bool TryNormalizeAxisConvertedSkeletonForReimport(
        string skeletonPath,
        int version,
        SkeletonData imported,
        out SkeletonData? originalSkeleton,
        out SkeletonData normalizedSkeleton,
        out string statusLine)
    {
        originalSkeleton = LoadSkeletonOrNull(skeletonPath, version);
        normalizedSkeleton = imported;
        statusLine = "";
        // (statusLine set below only when a normalization actually happens)
        if (originalSkeleton is null ||
            originalSkeleton.Bones.Count == 0 ||
            imported.Bones.Count == 0 ||
            !LooksLikeAxisConvertedRestPose(originalSkeleton, imported))
        {
            return false;
        }

        normalizedSkeleton = NormalizeAxisConvertedSkeleton(originalSkeleton, imported);
        statusLine = Loc.T("report.skeleton.rebuilt_axis_normalized", Path.GetFileName(skeletonPath));
        return true;
    }

    private static SkeletonData NormalizeAxisConvertedSkeleton(SkeletonData original, SkeletonData imported)
    {
        var importedByName = imported.Bones
            .Select((bone, index) => (bone, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.bone.Name))
            .GroupBy(item => item.bone.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var originalWorld = BuildSkeletonWorldPositions(original);
        var importedWorld = BuildSkeletonWorldPositions(imported);
        var normalized = new SkeletonData();
        var normalizedWorld = new Matrix4x4[original.Bones.Count];

        for (var i = 0; i < original.Bones.Count; i++)
        {
            var originalBone = original.Bones[i];
            var targetWorldPosition = !string.IsNullOrWhiteSpace(originalBone.Name) &&
                                      importedByName.TryGetValue(originalBone.Name, out var importedIndex)
                ? importedWorld[importedIndex]
                : originalWorld[i];

            var localPosition = new Vector3(originalBone.X, originalBone.Y, originalBone.Z);
            if (originalBone.ParentIndex >= 0 &&
                originalBone.ParentIndex < normalizedWorld.Length &&
                Matrix4x4.Invert(normalizedWorld[originalBone.ParentIndex], out var inverseParent))
            {
                localPosition = Vector3.Transform(targetWorldPosition, inverseParent);
            }
            else if (originalBone.ParentIndex < 0)
            {
                localPosition = targetWorldPosition;
            }

            var localRotation = NormalizeQuaternionOrIdentity(new Quaternion(
                originalBone.Qx,
                originalBone.Qy,
                originalBone.Qz,
                originalBone.Qw));
            var normalizedBone = originalBone with
            {
                X = localPosition.X,
                Y = localPosition.Y,
                Z = localPosition.Z,
                Qx = localRotation.X,
                Qy = localRotation.Y,
                Qz = localRotation.Z,
                Qw = localRotation.W,
            };
            normalized.Bones.Add(normalizedBone);

            var local = Matrix4x4.CreateFromQuaternion(localRotation) *
                        Matrix4x4.CreateTranslation(localPosition);
            normalizedWorld[i] = originalBone.ParentIndex >= 0 && originalBone.ParentIndex < normalizedWorld.Length
                ? local * normalizedWorld[originalBone.ParentIndex]
                : local;
        }

        return normalized;
    }

    private static bool LooksLikeAxisConvertedRestPose(SkeletonData original, SkeletonData imported)
    {
        const float WorldPositionTolerance = 0.0025f;
        const float AverageWorldPositionTolerance = 0.015f;
        const float LocalTranslationDelta = 0.005f;
        const float LocalRotationDeltaRadians = 0.2f;

        var originalByName = original.Bones
            .Select((bone, index) => (bone, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.bone.Name))
            .GroupBy(item => item.bone.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        if (originalByName.Count == 0)
        {
            return false;
        }

        var originalWorld = BuildSkeletonWorldPositions(original);
        var importedWorld = BuildSkeletonWorldPositions(imported);
        var matched = 0;
        var closeWorld = 0;
        var localTranslationChanged = 0;
        var localRotationChanged = 0;
        var totalWorldDistance = 0f;

        for (var importedIndex = 0; importedIndex < imported.Bones.Count; importedIndex++)
        {
            var importedBone = imported.Bones[importedIndex];
            if (string.IsNullOrWhiteSpace(importedBone.Name) ||
                !originalByName.TryGetValue(importedBone.Name, out var originalIndex))
            {
                continue;
            }

            matched++;
            var originalBone = original.Bones[originalIndex];
            var worldDistance = Vector3.Distance(originalWorld[originalIndex], importedWorld[importedIndex]);
            totalWorldDistance += worldDistance;
            if (worldDistance <= WorldPositionTolerance)
            {
                closeWorld++;
            }

            var localDistance = Vector3.Distance(
                new Vector3(originalBone.X, originalBone.Y, originalBone.Z),
                new Vector3(importedBone.X, importedBone.Y, importedBone.Z));
            if (localDistance >= LocalTranslationDelta)
            {
                localTranslationChanged++;
            }

            if (QuaternionDistanceRadians(
                    new Quaternion(originalBone.Qx, originalBone.Qy, originalBone.Qz, originalBone.Qw),
                    new Quaternion(importedBone.Qx, importedBone.Qy, importedBone.Qz, importedBone.Qw)) >= LocalRotationDeltaRadians)
            {
                localRotationChanged++;
            }
        }

        if (matched < Math.Min(20, original.Bones.Count * 3 / 4))
        {
            return false;
        }

        var averageWorldDistance = totalWorldDistance / matched;
        var sameRestPose =
            closeWorld >= matched * 3 / 5 &&
            averageWorldDistance <= AverageWorldPositionTolerance;
        if (!sameRestPose)
        {
            return false;
        }

        var divergentLocalCount = localTranslationChanged + localRotationChanged;
        return divergentLocalCount >= Math.Max(6, matched / 8);
    }

    private static Vector3[] BuildSkeletonWorldPositions(SkeletonData skeleton)
    {
        var matrices = new Matrix4x4[skeleton.Bones.Count];
        var state = new byte[skeleton.Bones.Count];
        for (var i = 0; i < skeleton.Bones.Count; i++)
        {
            BuildSkeletonWorldMatrix(skeleton, i, matrices, state);
        }

        var result = new Vector3[skeleton.Bones.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Vector3.Transform(Vector3.Zero, matrices[i]);
        }

        return result;
    }

    private static Matrix4x4 BuildSkeletonWorldMatrix(SkeletonData skeleton, int index, Matrix4x4[] matrices, byte[] state)
    {
        if (state[index] == 2)
        {
            return matrices[index];
        }

        if (state[index] == 1)
        {
            return Matrix4x4.Identity;
        }

        state[index] = 1;
        var bone = skeleton.Bones[index];
        var rotation = NormalizeQuaternionOrIdentity(new Quaternion(bone.Qx, bone.Qy, bone.Qz, bone.Qw));
        var local = Matrix4x4.CreateFromQuaternion(rotation) *
                    Matrix4x4.CreateTranslation(bone.X, bone.Y, bone.Z);
        if (bone.ParentIndex >= 0 && bone.ParentIndex < skeleton.Bones.Count)
        {
            matrices[index] = local * BuildSkeletonWorldMatrix(skeleton, bone.ParentIndex, matrices, state);
        }
        else
        {
            matrices[index] = local;
        }

        state[index] = 2;
        return matrices[index];
    }

    private static float QuaternionDistanceRadians(Quaternion a, Quaternion b)
    {
        a = NormalizeQuaternionOrIdentity(a);
        b = NormalizeQuaternionOrIdentity(b);
        var dot = Math.Abs(Quaternion.Dot(a, b));
        dot = Math.Clamp(dot, 0f, 1f);
        return 2f * MathF.Acos(dot);
    }

    private static Quaternion NormalizeQuaternionOrIdentity(Quaternion rotation)
        => rotation.LengthSquared() > 0.000001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;

    private static ReinsertTextureOptions BuildReinsertTextureOptions(bool useDiffuseAtlas, bool uncompressedTextures)
        => useDiffuseAtlas
            ? new ReinsertTextureOptions
            {
                NameMode = ReinsertTextureNameMode.PreferGltfNames,
                ForceUncompressed = uncompressedTextures,
                IncludedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "diffuse",
                    "detail_diffuse",
                    "tex7",
                    "tex8",
                    "bump",
                    "normal",
                    // Lightmap (bake) and baked shadow are per-object and not atlased, but must still be
                    // written/preserved so atlasing a lightmapped mesh does not drop its lightmap section.
                    "bake",
                    "shadow",
                },
            }
            : new ReinsertTextureOptions { ForceUncompressed = uncompressedTextures };

    // Name the atlas after existing template textures (the body/head diffuse + any existing normal) instead of
    // the generic "diffuse_atlas", so the atlas and its normal companion reuse real texture names the game
    // already references. Never a lines/detail map (ResolveAtlasTextureNames enforces it).
    private static GltfDiffuseAtlasResult ApplyDiffuseAtlasIfRequested(GltfModel model, bool useDiffuseAtlas, string templateMeshPath)
        => useDiffuseAtlas
            ? GltfDiffuseAtlasPacker.Pack(model, BuildAtlasOptions(templateMeshPath))
            : new GltfDiffuseAtlasResult(model, Applied: false, SourceTextureCount: 0, AtlasWidth: 0, AtlasHeight: 0, AtlasName: "", Warnings: []);

    private static bool ShouldAutoAtlasCombinedPart(
        GameConfig gameConfig,
        bool useDiffuseAtlas,
        bool externalSplit,
        GltfModel model,
        string templateMeshPath)
    {
        // TWAU pioneered the auto-atlas; MCSM Season 2 needs it for the same reason (foreign models
        // carry more diffuse textures than a v45 part's material slots can reference).
        if (useDiffuseAtlas ||
            gameConfig.Id is not (GameId.WolfAmongUs or GameId.MinecraftStoryModeSeason2) ||
            !externalSplit)
        {
            return false;
        }

        // v45 materials cannot gain texture slots (external .prop / fixed internal sets), so any
        // foreign part carrying more distinct diffuse textures than the template references must be
        // atlased into one — regardless of how the textures are named.
        if (gameConfig.Id == GameId.MinecraftStoryModeSeason2)
        {
            try
            {
                var v45TemplatePool = D3DMeshParser.ParseFile(templateMeshPath).Submeshes
                    .Select(sub => sub.TextureNames.TryGetValue("diffuse", out var name) ? name : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return ReinsertTextureService.DistinctV25TextureCount(model) > Math.Max(1, v45TemplatePool);
            }
            catch
            {
                return false;
            }
        }

        var templateSemantics = BuildTemplateTextureSemantics(templateMeshPath);
        if (templateSemantics.Count == 0)
        {
            return false;
        }

        foreach (var primitive in model.Primitives)
        {
            foreach (var image in primitive.TextureSlots.Values.Concat(primitive.ReferencedTextures.Values))
            {
                var semantic = ClassifyTextureSemantic(image.Name);
                if (semantic is null || IsGameProvidedTextureName(image.Name))
                {
                    continue;
                }

                if (!templateSemantics.Contains(semantic))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> BuildTemplateTextureSemantics(string templateMeshPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mesh = D3DMeshParser.ParseFile(templateMeshPath);
            foreach (var submesh in mesh.Submeshes)
            {
                foreach (var name in submesh.TextureNames.Values.Append(submesh.Name))
                {
                    if (ClassifyTextureSemantic(name) is { } semantic)
                    {
                        result.Add(semantic);
                    }
                }
            }
        }
        catch
        {
            return [];
        }

        return result;
    }

    private static string? ClassifyTextureSemantic(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lower = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (lower.Contains("eyelash") || lower.Contains("eyelashes")) return "eyelashes";
        if (lower.Contains("eye")) return "eye";
        if (lower.Contains("mouth") || lower.Contains("teeth") || lower.Contains("tongue")) return "mouth";
        if (lower.Contains("hair")) return "hair";
        if (lower.Contains("hand")) return "hands";
        if (lower.Contains("head") || lower.Contains("face")) return "head";
        if (lower.Contains("body") || lower.Contains("torso")) return "body";
        return null;
    }

    private static bool IsGameProvidedTextureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return stem.StartsWith("color_", StringComparison.Ordinal) ||
               stem.StartsWith("map_", StringComparison.Ordinal) ||
               stem.StartsWith("sk_sharedparts", StringComparison.Ordinal) ||
               stem.StartsWith("bmap_sk_sharedparts", StringComparison.Ordinal);
    }

    private static GltfDiffuseAtlasOptions BuildAtlasOptions(string templateMeshPath)
    {
        var names = ReinsertTextureService.ResolveAtlasTextureNames(templateMeshPath);
        const bool packSharedPartsTextures = true;
        return names is null
            ? new GltfDiffuseAtlasOptions(PackSharedPartsTextures: packSharedPartsTextures)
            : new GltfDiffuseAtlasOptions(AtlasName: names.Diffuse, NormalAtlasName: names.Normal, DetailAtlasName: names.Detail, PackSharedPartsTextures: packSharedPartsTextures);
    }

    private static string BuildAtlasStatusLine(GltfDiffuseAtlasResult atlas)
    {
        if (!atlas.Applied)
        {
            return atlas.SourceTextureCount == 1
                ? Loc.T("report.atlas.skipped_single")
                : "";
        }

        var line = Loc.T("report.atlas.packed", atlas.SourceTextureCount, atlas.AtlasWidth, atlas.AtlasHeight);
        if (atlas.Warnings.Count > 0)
        {
            line += Loc.T("report.atlas.warnings", string.Join(" ", atlas.Warnings.Take(3)));
        }

        return line;
    }

    private static Dictionary<string, List<GltfPrimitive>> BuildCombinedSourcePrimitiveMap(
        ModelAssetGroup group,
        GltfModel model,
        string inputRoot,
        out string modeLine,
        out bool externalSplit)
    {
        var result = new Dictionary<string, List<GltfPrimitive>>(StringComparer.OrdinalIgnoreCase);
        var groupPaths = group.Assets
            .Select(asset => Path.GetFullPath(asset.MeshPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var primitive in model.Primitives)
        {
            if (string.IsNullOrWhiteSpace(primitive.SourceMeshPath))
            {
                continue;
            }

            var source = primitive.SourceMeshPath!;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(inputRoot, source));
            if (!groupPaths.Contains(fullPath))
            {
                continue;
            }

            AddPrimitive(result, fullPath, primitive);
        }

        if (result.Values.Sum(static primitives => primitives.Count) == model.Primitives.Count)
        {
            modeLine = Loc.T("report.split_mode.original");
            externalSplit = false;
            return result;
        }

        var external = BuildExternalCombinedPrimitiveMap(group, model, inputRoot, result);
        modeLine = Loc.T("report.split_mode.external");
        externalSplit = true;
        return external;
    }

    private static Dictionary<string, List<GltfPrimitive>> BuildExternalCombinedPrimitiveMap(
        ModelAssetGroup group,
        GltfModel model,
        string inputRoot,
        Dictionary<string, List<GltfPrimitive>> exactMatches)
    {
        var result = exactMatches.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);

        var targets = group.Assets
            .Select(asset => BuildCombinedPartTarget(asset, inputRoot))
            .ToList();
        ExpandMissingCombinedPartSlots(targets);
        var mainTarget = targets
            .OrderByDescending(static target => target.IsMainPart)
            .ThenByDescending(static target => target.OriginalVertexCount)
            .First();

        // Same-game character swaps name their parts with a shared character prefix plus a part
        // suffix (skM1_aiden_eyes -> skM1_petra_eyes). Part tokens cannot tell apart parts sharing a
        // keyword ("eyes" vs "eyelids", the mouth visemes), and the alphabetical tie-break then sends
        // the iris quad into the eyelids file and leaves the eyes file invisible (white eyes
        // in-game). Exact part-suffix matches win first; tokens only handle what's left.
        var targetBySuffix = BuildTargetsByPartSuffix(targets);
        var sourcePartPrefix = LongestCommonFilenamePrefix(model.Primitives
            .Where(static primitive => !string.IsNullOrWhiteSpace(primitive.SourceMeshPath))
            .Select(static primitive => Path.GetFileNameWithoutExtension(primitive.SourceMeshPath!)));
        var targetPartPrefix = LongestCommonFilenamePrefix(group.Assets.Select(static asset => Path.GetFileNameWithoutExtension(asset.MeshPath)));
        var targetDir = Path.GetDirectoryName(group.Assets[0].MeshPath) ?? "";
        var groupPaths = group.Assets
            .Select(static asset => Path.GetFullPath(asset.MeshPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var alreadyAssigned = new HashSet<GltfPrimitive>(result.Values.SelectMany(static primitives => primitives));
        foreach (var primitive in model.Primitives)
        {
            if (!alreadyAssigned.Add(primitive))
            {
                continue;
            }

            var target = FindCombinedTargetByPartSuffix(primitive, sourcePartPrefix, targetBySuffix);
            int? targetSubmeshIndex = null;
            if (target is null &&
                ShouldLeavePrimitiveToCompanionPort(primitive, sourcePartPrefix, inputRoot, targetDir, targetPartPrefix, groupPaths))
            {
                continue;
            }

            if (target is null &&
                FindCombinedTargetByTemplateName(primitive, targets) is { } templateMatch)
            {
                target = templateMatch.Target;
                targetSubmeshIndex = templateMatch.SubmeshIndex;
            }
            target ??= FindBestCombinedPartTarget(primitive, targets) ?? mainTarget;
            AddPrimitive(result, target.FullPath, ClonePrimitiveForCombinedPart(primitive, targetSubmeshIndex));
        }

        return result;
    }

    private static bool ShouldPreserveCombinedEyeHelperPrimitives(GltfModel model, GameConfig gameConfig)
        => gameConfig.Id == GameId.WolfAmongUs &&
           model.Primitives.Any(static primitive =>
               string.IsNullOrWhiteSpace(primitive.SourceMeshPath) &&
               primitive.TextureSlots.TryGetValue("diffuse", out var diffuse) &&
               IsEyeHelperTextureName(diffuse.Name));

    private static bool IsEyeHelperTextureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals("map_1px_alpha", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("color_000", StringComparison.OrdinalIgnoreCase);
    }

    // A primitive whose part suffix matches a sibling file outside the group (e.g. the GLB carries
    // mouthDefault while this group's mouth slot is mouthNone) must not fall back onto head/main:
    // the companion porter rebuilds that sibling file from the primitive's own source mesh, and a
    // token-based assignment here would bake a second mouth into the head part.
    private static bool ShouldLeavePrimitiveToCompanionPort(
        GltfPrimitive primitive,
        string sourcePartPrefix,
        string inputRoot,
        string targetDir,
        string targetPartPrefix,
        IReadOnlySet<string> groupPaths)
    {
        if (!GameConfig.Current.PortCompanionVariantPartsOnReimport ||
            sourcePartPrefix.Length == 0 ||
            targetPartPrefix.Length == 0 ||
            targetDir.Length == 0 ||
            string.IsNullOrWhiteSpace(primitive.SourceMeshPath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(primitive.SourceMeshPath);
        if (name.Length <= sourcePartPrefix.Length ||
            !name.StartsWith(sourcePartPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sibling = Path.Combine(targetDir, targetPartPrefix + name[sourcePartPrefix.Length..] + ".d3dmesh");
        if (!File.Exists(sibling) || groupPaths.Contains(Path.GetFullPath(sibling)))
        {
            return false;
        }

        var donor = Path.GetFullPath(Path.IsPathRooted(primitive.SourceMeshPath)
            ? primitive.SourceMeshPath
            : Path.Combine(inputRoot, primitive.SourceMeshPath));
        return File.Exists(donor);
    }

    private static Dictionary<string, CombinedPartTarget> BuildTargetsByPartSuffix(IReadOnlyList<CombinedPartTarget> targets)
    {
        var result = new Dictionary<string, CombinedPartTarget>(StringComparer.OrdinalIgnoreCase);
        var prefix = LongestCommonFilenamePrefix(targets.Select(static target => Path.GetFileNameWithoutExtension(target.FullPath)));
        if (prefix.Length == 0)
        {
            return result;
        }

        foreach (var target in targets)
        {
            var name = Path.GetFileNameWithoutExtension(target.FullPath);
            if (name.Length > prefix.Length)
            {
                result.TryAdd(name[prefix.Length..], target);
            }
        }

        return result;
    }

    private static CombinedPartTarget? FindCombinedTargetByPartSuffix(
        GltfPrimitive primitive,
        string sourcePartPrefix,
        IReadOnlyDictionary<string, CombinedPartTarget> targetBySuffix)
    {
        if (sourcePartPrefix.Length == 0 ||
            targetBySuffix.Count == 0 ||
            string.IsNullOrWhiteSpace(primitive.SourceMeshPath))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(primitive.SourceMeshPath);
        if (name.Length <= sourcePartPrefix.Length ||
            !name.StartsWith(sourcePartPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return targetBySuffix.TryGetValue(name[sourcePartPrefix.Length..], out var target) ? target : null;
    }

    // Longest case-insensitive common prefix of a set of filenames. With a single name the whole name
    // is returned, which yields an empty part suffix and naturally disables suffix matching.
    private static string LongestCommonFilenamePrefix(IEnumerable<string> names)
    {
        string? prefix = null;
        foreach (var name in names)
        {
            if (prefix is null)
            {
                prefix = name;
                continue;
            }

            var max = Math.Min(prefix.Length, name.Length);
            var length = 0;
            while (length < max && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(name[length]))
            {
                length++;
            }

            prefix = prefix[..length];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return prefix ?? "";
    }

    private static CombinedPartTarget BuildCombinedPartTarget(ModelAsset asset, string inputRoot)
    {
        var mesh = D3DMeshParser.ParseFile(asset.MeshPath);
        var primaryLabels = new List<string>
        {
            Path.GetFileNameWithoutExtension(asset.MeshPath),
            Path.GetRelativePath(inputRoot, asset.MeshPath),
        };
        var tokens = BuildPartTokens(primaryLabels);
        var templateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateSubmeshByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var fallbackLabels = new List<string>();
        for (var submeshIndex = 0; submeshIndex < mesh.Submeshes.Count; submeshIndex++)
        {
            var submesh = mesh.Submeshes[submeshIndex];
            fallbackLabels.Add(submesh.Name);
            AddTemplateName(templateNames, templateSubmeshByName, submesh.Name, submeshIndex);
            if (!string.IsNullOrWhiteSpace(submesh.MaterialName))
            {
                fallbackLabels.Add(submesh.MaterialName!);
                AddTemplateName(templateNames, templateSubmeshByName, submesh.MaterialName, submeshIndex);
            }

            fallbackLabels.AddRange(submesh.TextureNames.Values);
            foreach (var textureName in submesh.TextureNames.Values)
            {
                AddTemplateName(templateNames, templateSubmeshByName, textureName, submeshIndex);
            }
        }

        if (tokens.Count == 0)
        {
            tokens = BuildPartTokens(fallbackLabels);
        }

        var isMain = tokens.Contains("body") ||
                     tokens.Contains("torso") ||
                     tokens.Contains("chest") ||
                     tokens.Contains("upper") ||
                     tokens.Contains("lower");

        return new CombinedPartTarget(
            asset,
            Path.GetFullPath(asset.MeshPath),
            tokens,
            templateNames,
            templateSubmeshByName,
            isMain,
            mesh.VertexCount);
    }

    private static CombinedPartTemplateMatch? FindCombinedTargetByTemplateName(GltfPrimitive primitive, IReadOnlyList<CombinedPartTarget> targets)
    {
        var labels = new List<string?>();
        labels.Add(primitive.MaterialName);
        labels.AddRange(primitive.TextureSlots.Values.Select(static image => image.Name));
        labels.AddRange(primitive.ReferencedTextures.Values.Select(static image => image.Name));

        var names = labels
            .Select(NormalizeTemplateMatchName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        CombinedPartTarget? best = null;
        int? bestSubmeshIndex = null;
        var bestScore = 0;
        var tied = false;
        foreach (var target in targets)
        {
            var score = names.Count(target.TemplateNames.Contains);
            if (score > bestScore)
            {
                best = target;
                bestSubmeshIndex = ResolveBestTemplateSubmeshIndex(names, target);
                bestScore = score;
                tied = false;
            }
            else if (score > 0 && score == bestScore)
            {
                tied = true;
            }
        }

        return bestScore > 0 && !tied && best is not null
            ? new CombinedPartTemplateMatch(best, bestSubmeshIndex)
            : null;
    }

    private static int? ResolveBestTemplateSubmeshIndex(IReadOnlyList<string> names, CombinedPartTarget target)
    {
        foreach (var name in names)
        {
            if (target.TemplateSubmeshByName.TryGetValue(name, out var submeshIndex))
            {
                return submeshIndex;
            }
        }

        return null;
    }

    private static void AddTemplateName(
        HashSet<string> names,
        Dictionary<string, int> submeshByName,
        string? name,
        int submeshIndex)
    {
        if (NormalizeTemplateMatchName(name) is { Length: > 0 } normalized)
        {
            names.Add(normalized);
            submeshByName.TryAdd(normalized, submeshIndex);
        }
    }

    private static string NormalizeTemplateMatchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        var generatedMarker = stem.IndexOf("__tt_", StringComparison.OrdinalIgnoreCase);
        if (generatedMarker > 0)
        {
            stem = stem[..generatedMarker];
        }

        return stem;
    }

    private static void ExpandMissingCombinedPartSlots(IReadOnlyList<CombinedPartTarget> targets)
    {
        CombinedPartTarget? Find(params string[] tokens)
            => targets.FirstOrDefault(target => tokens.Any(token => target.Tokens.Contains(token)));

        var main = targets
            .OrderByDescending(static target => target.IsMainPart)
            .ThenByDescending(static target => target.OriginalVertexCount)
            .FirstOrDefault();
        var head = Find("head") ?? main;
        var teeth = Find("teeth");

        if (head is not null)
        {
            foreach (var token in new[] { "hair", "eye", "brow", "eyelashes", "ear", "nose", "neck" })
            {
                if (Find(token) is null)
                {
                    head.Tokens.Add(token);
                }
            }
        }

        if (teeth is not null)
        {
            teeth.Tokens.Add("mouth");
            teeth.Tokens.Add("tongue");
        }
        else if (head is not null)
        {
            head.Tokens.Add("mouth");
            head.Tokens.Add("tongue");
        }

        if (main is not null)
        {
            foreach (var token in new[] { "hand", "arm", "leg", "foot" })
            {
                if (Find(token) is null)
                {
                    main.Tokens.Add(token);
                }
            }
        }
    }

    private static CombinedPartTarget? FindBestCombinedPartTarget(GltfPrimitive primitive, IReadOnlyList<CombinedPartTarget> targets)
    {
        var labels = new List<string?>();
        labels.Add(primitive.SourceMeshPath);
        labels.Add(primitive.MaterialName);
        labels.AddRange(primitive.TextureSlots.Values.Select(static image => image.Name));
        labels.AddRange(primitive.ReferencedTextures.Values.Select(static image => image.Name));

        var primitiveTokens = BuildPartTokens(labels.Where(static label => !string.IsNullOrWhiteSpace(label))!);
        if (primitiveTokens.Count == 0)
        {
            return null;
        }

        var bestScore = 0;
        CombinedPartTarget? best = null;
        foreach (var target in targets)
        {
            var score = target.Tokens.Sum(token => primitiveTokens.Contains(token) ? TokenWeight(token) : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static HashSet<string> BuildPartTokens(IEnumerable<string> labels)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            foreach (var token in SplitPartLabel(label))
            {
                AddPartToken(tokens, token);
            }
        }

        return tokens;
    }

    private static IEnumerable<string> SplitPartLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            yield break;
        }

        var chars = label.Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray();
        foreach (var raw in new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length >= 2)
            {
                yield return raw;
            }
        }
    }

    private static void AddPartToken(HashSet<string> tokens, string token)
    {
        token = token.Trim().ToLowerInvariant();
        if (token.Length < 2 || token.StartsWith("sk", StringComparison.OrdinalIgnoreCase) || token.All(char.IsDigit))
        {
            return;
        }

        static bool Has(string value, string part) => value.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (Has(token, "body")) tokens.Add("body");
        if (Has(token, "torso")) tokens.Add("torso");
        if (Has(token, "chest")) tokens.Add("chest");
        if (Has(token, "upper")) tokens.Add("upper");
        if (Has(token, "lower")) tokens.Add("lower");
        if (Has(token, "head")) tokens.Add("head");
        if (Has(token, "face")) tokens.Add("head");
        if (Has(token, "hair")) tokens.Add("hair");
        if (Has(token, "eyelash") || Has(token, "eyelashes")) tokens.Add("eyelashes");
        if (Has(token, "hat")) tokens.Add("hat");
        if (Has(token, "hand")) tokens.Add("hand");
        // "armor" must not register as "arm": a part named shoulderArmor would otherwise score as
        // an arm slot and swallow the imported model's arms/hands (MCSM S2 Lukas). Armour and
        // shoulder get their own tokens so armour parts still match armour parts.
        if (Has(token, "armor") || Has(token, "armour")) tokens.Add("armor");
        if (Has(token, "shoulder")) tokens.Add("shoulder");
        if (token.Replace("armour", string.Empty).Replace("armor", string.Empty).Contains("arm", StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add("arm");
        }
        if (Has(token, "leg")) tokens.Add("leg");
        if (Has(token, "foot") || Has(token, "feet")) tokens.Add("foot");
        if (Has(token, "beard")) tokens.Add("beard");
        if (Has(token, "eye")) tokens.Add("eye");
        if (Has(token, "mouth")) tokens.Add("mouth");
        if (Has(token, "teeth")) tokens.Add("teeth");
        if (Has(token, "tongue")) tokens.Add("tongue");
        if (Has(token, "brow")) tokens.Add("brow");
        // Same containment trap as arm/armor: "beard" ends with "ear".
        if (token.Replace("beard", string.Empty).Contains("ear", StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add("ear");
        }
        if (Has(token, "neck")) tokens.Add("neck");
        if (Has(token, "nose")) tokens.Add("nose");
        if (Has(token, "cloth") || Has(token, "shirt") || Has(token, "coat") || Has(token, "jacket")) tokens.Add("body");
    }

    private static int TokenWeight(string token)
        => token is "body" or "torso" or "head" or "hair" or "hat" or "hand" or "arm" or "leg" or "foot" or "eyelashes" ? 3 : 1;

    private static GltfPrimitive ClonePrimitiveForCombinedPart(GltfPrimitive source, int? targetSubmeshIndex)
    {
        return new GltfPrimitive
        {
            Positions = source.Positions,
            Normals = source.Normals,
            Uv0 = source.Uv0,
            Uv1 = source.Uv1,
            Uv2 = source.Uv2,
            Uv3 = source.Uv3,
            Color0 = source.Color0,
            Tangents = source.Tangents,
            Binormals = source.Binormals,
            Unknown1 = source.Unknown1,
            Joints0 = source.Joints0,
            Weights0 = source.Weights0,
            Indices = source.Indices,
            MaterialName = source.MaterialName,
            BonePaletteIndex = null,
            SourceMeshPath = null,
            SourceSubmeshIndex = targetSubmeshIndex,
            RecoveredDetailLineTextureName = source.RecoveredDetailLineTextureName,
            RecoveredDetailLineImage = source.RecoveredDetailLineImage,
            IsSkinned = source.IsSkinned,
            BaseColor = source.BaseColor,
            TextureSlots = source.TextureSlots,
            ReferencedTextures = source.ReferencedTextures,
        };
    }

    private static void AddPrimitive(Dictionary<string, List<GltfPrimitive>> map, string fullPath, GltfPrimitive primitive)
    {
        if (!map.TryGetValue(fullPath, out var primitives))
        {
            primitives = [];
            map[fullPath] = primitives;
        }

        primitives.Add(primitive);
    }

    private sealed record CombinedPartTarget(
        ModelAsset Asset,
        string FullPath,
        HashSet<string> Tokens,
        HashSet<string> TemplateNames,
        Dictionary<string, int> TemplateSubmeshByName,
        bool IsMainPart,
        int OriginalVertexCount);

    private sealed record CombinedPartTemplateMatch(CombinedPartTarget Target, int? SubmeshIndex);

    private string? ChooseOutputFolder()
    {
        var selectedPath = SuggestedOutputFolder();
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("dialog.extract_output.description"),
            UseDescriptionForTitle = true,
            SelectedPath = selectedPath
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        _lastOutputFolder = dialog.SelectedPath;
        return dialog.SelectedPath;
    }

    private string SuggestedOutputFolder()
    {
        if (_lastOutputFolder is not null && Directory.Exists(_lastOutputFolder))
        {
            return _lastOutputFolder;
        }

        if (_rootFolder is not null && Directory.Exists(_rootFolder))
        {
            return _rootFolder;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TelltaleD3DMeshEditor");
    }

    private string? ChooseReimportInputFile(string? initialDirectory)
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("dialog.reimport_input.title"),
            Filter = Loc.T("dialog.reimport_input.filter"),
            InitialDirectory = _lastOutputFolder ?? initialDirectory,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string SuggestedReimportOutputFolder(string fallbackFolder)
    {
        if (_lastOutputFolder is not null && Directory.Exists(_lastOutputFolder))
        {
            return _lastOutputFolder;
        }

        return fallbackFolder;
    }

    private string? ChooseReimportOutputFolder(string fallbackFolder)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("dialog.reimport_output.description"),
            UseDescriptionForTitle = true,
            SelectedPath = SuggestedReimportOutputFolder(fallbackFolder)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        _lastOutputFolder = dialog.SelectedPath;
        return dialog.SelectedPath;
    }

    private static SkeletonData? LoadSkeletonOrNull(string? skeletonPath, int version)
    {
        return string.IsNullOrWhiteSpace(skeletonPath) || !File.Exists(skeletonPath)
            ? null
            : SkeletonLoader.Load(skeletonPath, version);
    }

    private async Task RunWithUiLockAsync(Func<Task<string>> operation)
    {
        SetBusy(true);
        try
        {
            var message = await operation();
            SetStatusText(Loc.T("status.done"));
            MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatusText(Loc.T("status.error"));
            var logPath = ErrorLog.Write(ex, "Operation failed");
            MessageBox.Show(
                Loc.T("msg.operation_failed", logPath, ex.Message),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            _searchDebounceTimer.Stop();
        }

        _isBusy = busy;
        UseWaitCursor = busy;
        _btnOpen.Enabled = !busy;
        _btnOpenArchive.Enabled = !busy;
        _btnFilter.Enabled = !busy && _rootFolder is not null;
        _btnReload.Enabled = !busy && _rootFolder is not null;
        _btnExtractAll.Enabled = !busy && _rootFolder is not null && _assets.Count > 0;
        _btnExtractSelected.Enabled = !busy && _rootFolder is not null && HasExtractSelection();
        _btnReimportSelected.Enabled = !busy && ReinsertionSupported &&
                                      (GetSingleSelectedAssetForReimport() is not null ||
                                       GetSingleSelectedGroupForReimport() is not null);
        _btnCombineParts.Enabled = !busy && _rootFolder is not null && _assets.Count > 0;
        _btnPan.Enabled = !busy;
        _btnPose.Enabled = !busy;
        _btnView.Enabled = !busy;
        _btnCredits.Enabled = !busy;
        _btnReportIssue.Enabled = !busy;
        _btnCheckUpdates.Enabled = !busy;
        _btnSettings.Enabled = !busy;
        _gameSelector.Enabled = !busy;
        _searchText.Enabled = !busy && _rootFolder is not null;

        if (busy)
        {
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            _progress.Value = 0;
            _progressLabel.Text = Loc.T("status.working_zero");
            _progress.Visible = true;
            _progressLabel.Visible = true;
        }
        else
        {
            _progress.Visible = false;
            _progressLabel.Visible = false;
            _progressLabel.Text = "";
            _progressLabel.ToolTipText = "";
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
    }

    // Updates the deterministic progress bar. Safe to call from a Progress<T> callback (UI thread).
    private void SetProgress(int done, int total, string label)
    {
        if (!_isBusy || total <= 0)
        {
            return;
        }

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Minimum = 0;
        _progress.Maximum = total;
        _progress.Value = Math.Clamp(done, 0, total);
        _progressLabel.Text = label;
        _progressLabel.ToolTipText = label;
        _progress.Visible = true;
        _progressLabel.Visible = true;
    }

    // Looks for a newer GitHub release. When silent (startup), stays quiet unless an update is found;
    // when triggered from the menu, also confirms when the tool is already up to date.
    private async Task CheckForUpdatesAsync(bool silent)
    {
        var info = await UpdateChecker.FetchLatestReleaseAsync();
        if (info is null)
        {
            if (!silent)
            {
                MessageBox.Show(
                    Loc.T("msg.update.check_failed"),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return;
        }

        if (UpdateChecker.IsNewerVersion(info.Version, UpdateChecker.CurrentVersion))
        {
            ShowUpdateDialog(info);
            return;
        }

        if (!silent)
        {
            ShowUpToDateDialog(info);
        }
    }

#if DEBUG
    // Debug-only test harness: fetches the latest published release (ignoring the version comparison) and
    // shows the normal update dialog, so the update experience can be rehearsed on demand.
    private async Task SimulateUpdateAsync()
    {
        var info = await UpdateChecker.FetchLatestReleaseAsync();
        if (info is null)
        {
            MessageBox.Show(
                Loc.T("msg.update.simulate_fetch_failed"),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ShowUpdateDialog(info);
    }
#endif

    // Shows the new version and its changelog, and lets the user install it or open the release page.
    // Installation only starts after the user clicks the button and confirms the restart.
    private void ShowUpdateDialog(UpdateInfo info)
    {
        using var dialog = new Form
        {
            Text = Loc.T("dialog.update.title"),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 470),
            MinimumSize = new Size(440, 320),
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16, 14, 16, 14),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Text = Loc.T("dialog.update.header", info.Title),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };

        var subtitle = new Label
        {
            Text = Loc.T("dialog.update.subtitle", UpdateChecker.CurrentVersion, info.Version),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };

        // Render the release notes as HTML so the dialog mirrors the GitHub page (headings, list, table,
        // and the banner image at its intended size) instead of dumping the raw Markdown into a textbox.
        var changelogFrame = new Panel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(0),
        };
        var changelog = new WebBrowser
        {
            Dock = DockStyle.Fill,
            ScriptErrorsSuppressed = true,
            AllowWebBrowserDrop = false,
            IsWebBrowserContextMenuEnabled = false,
            WebBrowserShortcutsEnabled = false,
        };
        // External links in the notes open in the user's real browser, not inside this control.
        changelog.NewWindow += (_, e) => ((System.ComponentModel.CancelEventArgs)e).Cancel = true;
        changelog.Navigating += (_, e) =>
        {
            if (!string.IsNullOrEmpty(changelog.DocumentText) &&
                e.Url is { Scheme: "http" or "https" })
            {
                e.Cancel = true;
                OpenUrl(e.Url.ToString());
            }
        };
        changelogFrame.Controls.Add(changelog);
        // Set the document once the dialog is shown, so the browser control's window handle already exists.
        dialog.Shown += (_, _) => changelog.DocumentText = ReleaseNotesHtml.Build(info.Changelog);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
        };
        var close = new Button { Text = Loc.T("common.close"), AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6, 0, 0, 0) };
        var viewOnGitHub = new Button { Text = Loc.T("common.view_on_github"), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var download = new Button
        {
            Text = string.IsNullOrEmpty(info.DownloadUrl) ? Loc.T("dialog.update.open_download") : Loc.T("dialog.update.install_update"),
            AutoSize = true,
            Margin = new Padding(6, 0, 0, 0)
        };
        viewOnGitHub.Click += (_, _) => OpenUrl(info.ReleaseUrl);
        download.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                OpenUrl(info.ReleaseUrl);
                return;
            }

            dialog.Close();
            await InstallUpdateAsync(info);
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(viewOnGitHub);
        buttons.Controls.Add(download);

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(subtitle, 0, 1);
        layout.Controls.Add(changelogFrame, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private void ShowUpToDateDialog(UpdateInfo info)
    {
        using var dialog = new Form
        {
            Text = Loc.T("dialog.uptodate.title"),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 14, 16, 14),
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Text = Loc.T("dialog.uptodate.header", UpdateChecker.CurrentVersion),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };

        var reinstallHint = new Label
        {
            Text = string.IsNullOrWhiteSpace(info.DownloadUrl)
                ? Loc.T("dialog.uptodate.no_installer")
                : Loc.T("dialog.uptodate.reinstall_hint"),
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 14),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };
        var close = new Button { Text = Loc.T("common.close"), AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6, 0, 0, 0) };
        var viewOnGitHub = new Button { Text = Loc.T("common.view_on_github"), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        viewOnGitHub.Click += (_, _) => OpenUrl(info.ReleaseUrl);
        buttons.Controls.Add(close);
        buttons.Controls.Add(viewOnGitHub);

        if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            var reinstall = new Button { Text = Loc.T("dialog.uptodate.reinstall_button"), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            reinstall.Click += async (_, _) =>
            {
                dialog.Close();
                await InstallUpdateAsync(info, reinstall: true);
            };
            buttons.Controls.Add(reinstall);
        }

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(reinstallHint, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private async Task InstallUpdateAsync(UpdateInfo info, bool reinstall = false)
    {
        var title = reinstall ? Loc.T("dialog.install.title_reinstall") : Loc.T("dialog.install.title_install");
        var prompt = reinstall
            ? Loc.T("dialog.install.prompt_reinstall", info.Title)
            : Loc.T("dialog.install.prompt_install", info.Title);
        var answer = MessageBox.Show(
            prompt,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            SetStatusText(reinstall ? Loc.T("status.reinstalling", info.Version) : Loc.T("status.installing", info.Version));
            var progress = new Progress<(int Done, int Total, string Label)>(p =>
                SetProgress(p.Done, p.Total, p.Label));
            await SelfUpdater.DownloadExtractAndRestartAsync(info, progress);
            SetStatusText(reinstall ? Loc.T("status.restarting_reinstall") : Loc.T("status.restarting_update"));
            Application.Exit();
        }
        catch (Exception ex)
        {
            SetStatusText(reinstall ? Loc.T("status.reinstall_failed") : Loc.T("status.update_failed"));
            MessageBox.Show(
                reinstall ? Loc.T("msg.update.reinstall_failed_body", ex.Message) : Loc.T("msg.update.install_failed_body", ex.Message),
                reinstall ? Loc.T("msg.update.reinstall_failed_title") : Loc.T("msg.update.update_failed_title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            OpenUrl(info.ReleaseUrl);
            SetBusy(false);
        }
    }

    // Reads the UTC build timestamp stamped into the assembly at compile time and converts it to the
    // local timezone, so the build time in the title shows correctly for every user. Falls back to the
    // file's last-write time if the stamp is missing.
    private static DateTime GetLocalBuildTime()
    {
        var stamp = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimestampUtc")?.Value;

        if (stamp is not null &&
            DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var built))
        {
            return built.ToLocalTime().DateTime;
        }

        return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location);
    }

    private static string GetBuildLabel()
    {
#if DEBUG
        return "DEBUG";
#else
        return GetLocalBuildTime().ToString("yyyy-MM-dd HH:mm:ss");
#endif
    }

    // Keeps the Pan/Pose/View overlay pinned to the bottom-right corner of the preview as it resizes.
    private void PositionViewerOverlay()
    {
        const int margin = 8;
        _viewerOverlay.Left = Math.Max(0, _preview.ClientSize.Width - _viewerOverlay.Width - margin);
        _viewerOverlay.Top = Math.Max(0, _preview.ClientSize.Height - _viewerOverlay.Height - margin);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is best-effort; ignore failures.
        }
    }

    private void ShowReportIssueDialog()
    {
        var reportTime = DateTime.Now;

        using var dialog = new Form
        {
            Text = Loc.T("dialog.report.title"),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Icon = LoadDialogIcon("logo - issue.ico"),
            ClientSize = new Size(680, 430),
            MinimumSize = new Size(560, 360),
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(14),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = Loc.T("dialog.report.intro"),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);

        var gameLabel = new Label
        {
            Text = Loc.T("dialog.report.game"),
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, 4, 8, 8),
        };
        var gameCombo = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 0, 0, 8),
        };
        var supportedGameNames = GameConfig.All
            .Where(game => game.Id != GameId.Generic && !game.IsGameMenuGroup)
            .Select(game => game.DisplayName)
            .ToList();
        foreach (var gameName in supportedGameNames)
        {
            gameCombo.Items.Add(gameName);
        }

        var selectedGameName = GameConfig.Current.Id == GameId.Generic ? supportedGameNames.FirstOrDefault() : GameConfig.Current.DisplayName;
        if (selectedGameName is not null)
        {
            gameCombo.SelectedItem = selectedGameName;
            if (gameCombo.SelectedIndex < 0 && gameCombo.Items.Count > 0)
            {
                gameCombo.SelectedIndex = 0;
            }
        }
        layout.Controls.Add(gameLabel, 0, 1);
        layout.Controls.Add(gameCombo, 1, 1);

        var descriptionLabel = new Label
        {
            Text = Loc.T("dialog.report.description"),
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, 4, 8, 8),
        };
        var descriptionText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            AcceptsTab = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(descriptionLabel, 0, 2);
        layout.Controls.Add(descriptionText, 1, 2);

        var automaticLabel = new Label
        {
            Text = Loc.T("dialog.report.auto_info"),
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, 4, 8, 8),
        };
        var automaticInfoPanel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Top,
            Padding = new Padding(6),
            Margin = new Padding(0, 0, 0, 10),
        };
        var automaticInfo = new Label
        {
            AutoSize = true,
            Text = BuildIssueAutomaticInfo(reportTime),
            Margin = new Padding(0),
            UseMnemonic = false,
        };
        automaticInfoPanel.Controls.Add(automaticInfo);
        layout.Controls.Add(automaticLabel, 0, 3);
        layout.Controls.Add(automaticInfoPanel, 1, 3);

        var note = new Label
        {
            Text = Loc.T("dialog.report.note"),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        layout.Controls.Add(note, 0, 4);
        layout.SetColumnSpan(note, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
        };
        var cancel = new Button { Text = Loc.T("common.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var submit = new Button { Text = Loc.T("dialog.report.submit"), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        submit.Click += (_, _) =>
        {
            var description = descriptionText.Text.Trim();
            if (description.Length == 0)
            {
                MessageBox.Show(
                    Loc.T("msg.report.describe_first"),
                    dialog.Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                descriptionText.Focus();
                return;
            }

            var gameName = (gameCombo.SelectedItem as string ?? "").Trim();
            var title = $"Bug report: {gameName}";
            var body = BuildIssueBody(gameName, description, reportTime);
            OpenUrl(BuildIssueUrl(title, body));
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(submit);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        dialog.ShowDialog(this);
    }

    private string BuildIssueAutomaticInfo(DateTime reportTime)
    {
        var lines = new List<string>
        {
            $"Selected game: {GameConfig.Current.DisplayName}",
            $"Tool version: v{UpdateChecker.CurrentVersion}",
            $"Build: {GetBuildLabel()}",
            $"Report time: {reportTime:yyyy-MM-dd HH:mm:ss zzz}",
            $"Loaded folder: {GetSafeFolderName(_rootFolder)}",
            $"Current selection: {GetIssueSelectionText()}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildIssueBody(string gameName, string description, DateTime reportTime)
    {
        return $"""
        ## Game
        {gameName}

        ## What happened
        {description}

        ## Attachments
        Paste screenshots here with Ctrl+V, or drag image/log files into this section before submitting the issue.

        ## Automatic tool information
        - Selected game in tool: {GameConfig.Current.DisplayName}
        - Tool version: v{UpdateChecker.CurrentVersion}
        - Build: {GetBuildLabel()}
        - Report time: {reportTime:yyyy-MM-dd HH:mm:ss zzz}
        - Loaded folder: {GetSafeFolderName(_rootFolder)}
        - Current selection: {GetIssueSelectionText()}
        """;
    }

    private string GetIssueSelectionText()
    {
        if (_selectedAsset is not null)
        {
            return Path.GetFileName(_selectedAsset.MeshPath);
        }

        if (_selectedGroup is not null)
        {
            return _selectedGroup.ToString();
        }

        return "None";
    }

    private static string GetSafeFolderName(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "None";
        }

        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name)
            ? "None"
            : name;
    }

    private static string BuildLoadedFolderStatus(string? folder)
        => Loc.T("status.loaded_folder", GetSafeFolderName(folder));

    private static string BuildIssueUrl(string title, string body)
    {
        const string newIssueUrl = "https://github.com/HeitorSpectre/Telltale-D3DMesh-Editor/issues/new";
        return newIssueUrl
            + "?title=" + Uri.EscapeDataString(title)
            + "&body=" + Uri.EscapeDataString(body);
    }

    private void ShowDiscordInviteIfDue()
    {
        var preferences = AppPreferences.Load();
        if (!ShouldShowDiscordInvite(preferences))
        {
            return;
        }

        using var dialog = new DiscordInviteDialog(Font);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AppPreferences.SaveDiscordInviteAccepted();
            OpenUrl(DiscordInviteUrl);
            return;
        }

        AppPreferences.SaveDiscordInviteDismissed();
    }

#if DEBUG
    private void SimulateDiscordInvite()
    {
        using var dialog = new DiscordInviteDialog(Font);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            OpenUrl(DiscordInviteUrl);
        }
    }
#endif

    private static bool ShouldShowDiscordInvite(AppPreferences preferences)
    {
        if (preferences.DiscordInviteAccepted)
        {
            return false;
        }

        if (preferences.DiscordInviteLastDismissedUtc is not { } dismissedUtc)
        {
            return true;
        }

        return DateTime.UtcNow - dismissedUtc.ToUniversalTime() >= TimeSpan.FromDays(DiscordInviteReminderDays);
    }

    private static Icon LoadDialogIcon(string iconFileName)
        => EmbeddedIconResources.LoadIcon(iconFileName == "logo - issue.ico"
            ? EmbeddedIconResources.Issue
            : EmbeddedIconResources.D3DMesh);

    private void ShowCreditsDialog()
    {
        using var dialog = new Form
        {
            Text = Loc.T("credits.title"),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 1),
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 14, 16, 14),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = Loc.T("app.title"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };

        const string authorName = "HeitorSpectre";
        const string authorUrl = "https://github.com/HeitorSpectre";
        var madeByText = Loc.T("credits.made_by", authorName);
        var authorLinkStart = madeByText.IndexOf(authorName, StringComparison.Ordinal);
        var madeBy = new LinkLabel
        {
            Text = madeByText,
            AutoSize = true,
            // Link only the author name, located within the (possibly translated) sentence.
            LinkArea = authorLinkStart >= 0 ? new LinkArea(authorLinkStart, authorName.Length) : new LinkArea(0, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        madeBy.Links[0].LinkData = authorUrl;
        madeBy.LinkClicked += (_, e) =>
        {
            if (e.Link?.LinkData is string target)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        };
        WireCreditLinkStatus(madeBy, authorUrl);

        var thanks = new Label
        {
            Text = Loc.T("credits.special_thanks_to"),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 5),
        };

        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10),
        };
        AddCreditLink(links, "iMrShadow", "https://github.com/iMrShadow");
        AddCreditLink(links, "Gamma_02", "https://github.com/gamma-02");
        AddCreditLink(links, "David Matos", "https://github.com/frostbone25");
        AddCreditLink(links, "RandomTBush", "https://github.com/RandomTBush");
        AddCreditLink(links, "Aabii / Arizzble", "https://github.com/Arizzble");

        var paragraph = new Label
        {
            Text = Loc.T("credits.paragraph"),
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Margin = new Padding(0, 0, 0, 10),
        };

        const string telltaleName = "Telltale Games";
        const string skunkapeName = "Skunkape Games";
        const string telltaleUrl = "https://telltale.com";
        const string skunkapeUrl = "https://skunkapegames.com";
        var specialThanksText = Loc.T("credits.special_thanks", telltaleName, skunkapeName);
        var specialThanks = new CreditLinkLabel
        {
            Text = specialThanksText,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Margin = new Padding(0, 0, 0, 12),
        };
        var telltaleStart = specialThanksText.IndexOf(telltaleName, StringComparison.Ordinal);
        var skunkapeStart = specialThanksText.IndexOf(skunkapeName, StringComparison.Ordinal);
        specialThanks.Links.Add(telltaleStart, telltaleName.Length, telltaleUrl);
        specialThanks.Links.Add(skunkapeStart, skunkapeName.Length, skunkapeUrl);
        specialThanks.LinkClicked += (_, e) =>
        {
            if (e.Link?.LinkData is string target)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        };
        WireCreditLinkStatusByLink(specialThanks);

        var legalFooter = new Label
        {
            Text = Loc.T("credits.legal_footer", DateTime.Now.Year),
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = SystemColors.GrayText,
            Font = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f), FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 12),
        };

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0),
            WrapContents = false,
        };
        var ok = new Button
        {
            Text = Loc.T("common.ok"),
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        buttons.Controls.Add(ok);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(madeBy, 0, 1);
        layout.Controls.Add(thanks, 0, 2);
        layout.Controls.Add(links, 0, 3);
        layout.Controls.Add(paragraph, 0, 4);
        layout.Controls.Add(specialThanks, 0, 5);
        layout.Controls.Add(legalFooter, 0, 6);
        layout.Controls.Add(buttons, 0, 7);

        dialog.Controls.Add(layout);
        dialog.ClientSize = new Size(540, layout.GetPreferredSize(new Size(540, 0)).Height);
        dialog.AcceptButton = ok;
        dialog.ShowDialog(this);
    }

    private void AddCreditLink(FlowLayoutPanel links, string name, string url)
    {
        var link = new LinkLabel
        {
            Text = $"{name} - {url}",
            AutoSize = true,
            LinkArea = new LinkArea(name.Length + 3, url.Length),
            Margin = new Padding(0, 0, 0, 3),
        };
        link.Links[0].LinkData = url;
        link.LinkClicked += (_, e) =>
        {
            if (e.Link?.LinkData is string target)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        };
        WireCreditLinkStatus(link, url);
        links.Controls.Add(link);
    }

    private void WireCreditLinkStatus(Control control, string statusText)
    {
        control.MouseEnter += (_, _) => ShowCreditLinkStatus(statusText);
        control.MouseLeave += (_, _) => RestoreCreditLinkStatus();
    }

    private void WireCreditLinkStatusByLink(CreditLinkLabel control)
    {
        control.MouseMove += (_, e) =>
        {
            if (control.LinkAt(e.Location)?.LinkData is string target)
            {
                ShowCreditLinkStatus(target);
            }
            else
            {
                RestoreCreditLinkStatus();
            }
        };
        control.MouseLeave += (_, _) => RestoreCreditLinkStatus();
    }

    private void ShowCreditLinkStatus(string statusText)
    {
        _statusTextBeforeCreditLinkHover ??= _statusLabel.Text;
        SetStatusText(statusText);
    }

    private void RestoreCreditLinkStatus()
    {
        if (_statusTextBeforeCreditLinkHover is not { } previous)
        {
            return;
        }

        _statusTextBeforeCreditLinkHover = null;
        SetStatusText(previous);
    }

    private sealed class CreditLinkLabel : LinkLabel
    {
        public Link? LinkAt(Point location) => PointInLink(location.X, location.Y);
    }

    private void AutoFitTreeWidth()
    {
        // While minimized the form/splitter width collapses to ~0, leaving no valid SplitterDistance range
        // (Panel1MinSize > Width - Panel2MinSize); skip rather than throw if a load finishes meanwhile.
        if (WindowState == FormWindowState.Minimized)
        {
            return;
        }

        if (_tree.Nodes.Count == 0)
        {
            EnsureTreePanelWidth();
            return;
        }

        // The SplitContainer only accepts a distance in [Panel1MinSize, Width - Panel2MinSize - SplitterWidth].
        // If the window is too narrow for any valid value, leave the splitter as-is.
        var splitMax = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
        if (splitMax < _split.Panel1MinSize)
        {
            return;
        }

        var max = 0;
        var measured = 0;
        using (var graphics = _tree.CreateGraphics())
        {
            MeasureVisibleNodes(_tree.Nodes, graphics, _tree.Font, 0, ref max, ref measured, MaxTreeNodesToMeasureForAutoFit);
        }

        var target = max + SystemInformation.VerticalScrollBarWidth + 42;
        var minWidth = MinimumTreePanelWidth;
        var maxWidth = Math.Max(minWidth, ClientSize.Width / 2);
        target = Math.Clamp(target, minWidth, maxWidth);
        target = Math.Clamp(target, _split.Panel1MinSize, splitMax);
        if (Math.Abs(_split.SplitterDistance - target) > 4)
        {
            _split.SplitterDistance = target;
        }
    }

    private void ScheduleTreeAutoFit()
    {
        _treeFitDebounceTimer.Stop();
        _treeFitDebounceTimer.Start();
    }

    private void EnsureTreePanelWidth()
    {
        // No valid SplitterDistance range while minimized / too narrow (Panel1MinSize > usable width):
        // setting it would throw, so leave the splitter untouched.
        var splitMax = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
        if (WindowState == FormWindowState.Minimized || splitMax < _split.Panel1MinSize)
        {
            return;
        }

        var maxWidth = Math.Max(MinimumTreePanelWidth, splitMax);
        var target = Math.Clamp(PreferredTreePanelWidth, MinimumTreePanelWidth, maxWidth);
        target = Math.Clamp(target, _split.Panel1MinSize, splitMax);
        if (_split.SplitterDistance < target)
        {
            _split.SplitterDistance = target;
        }
    }

    private static void MeasureVisibleNodes(
        TreeNodeCollection nodes,
        Graphics graphics,
        Font font,
        int depth,
        ref int maxPx,
        ref int measured,
        int maxNodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (measured++ >= maxNodes)
            {
                return;
            }

            var width = (int)graphics.MeasureString(node.Text, font).Width + depth * 19;
            if (width > maxPx)
            {
                maxPx = width;
            }

            if (node.IsExpanded)
            {
                MeasureVisibleNodes(node.Nodes, graphics, font, depth + 1, ref maxPx, ref measured, maxNodes);
                if (measured >= maxNodes)
                {
                    return;
                }
            }
        }
    }
}
