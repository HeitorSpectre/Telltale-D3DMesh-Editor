using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using TelltaleD3DMeshEditor.Core;
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

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStrip _viewerOverlay = new();
    private readonly ToolStripButton _btnOpen = new("Open Folder...");
    private readonly ToolStripButton _btnOpenArchive = new("Open Archive...");
    private readonly ToolStripButton _btnExtractSelected = new("Extract Selected...");
    private readonly ToolStripButton _btnExtractAll = new("Extract All...");
    private readonly ToolStripButton _btnReimportSelected = new("Reimport Selected...");
    private readonly ToolStripButton _btnDiffuseAtlas = new("Texture Atlas");
    private readonly ToolStripButton _btnCombineParts = new("Combine Parts");
    private readonly ToolStripDropDownButton _gameSelector = new();
    private readonly Button _btnReload = new() { Text = "Reload" };
    private readonly ToolStripButton _btnPan = new("Pan");
    private readonly ToolStripButton _btnPose = new("Pose");
    private readonly ToolStripDropDownButton _btnView = new("View");
    private readonly ToolStripLabel _progressLabel = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripButton _btnCredits = new("Credits");
    private readonly ToolStripMenuItem _miFit = new("Center");
    private readonly ToolStripMenuItem _miFaces = new("Solid");
    private readonly ToolStripMenuItem _miPolygons = new("Polygons");
    private readonly ToolStripMenuItem _miSkeleton = new("Skeleton");
    private readonly ToolStripButton _btnCheckUpdates = new("Check for Updates");
    private readonly ToolStripButton _btnSettings = new("Settings");
    private readonly SplitContainer _split = new();
    private readonly TreeView _tree = new();
    private readonly TextBox _searchText = new();
    private readonly System.Windows.Forms.Timer _searchDebounceTimer = new();
    private readonly MeshPreviewControl _preview = new();
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
    private bool _isBusy;
    private bool _applyingTreeSelection;

    public MainForm()
    {
        // Title shows the version and the build tag. Release builds show the build time (in the user's own
        // timezone); Debug builds show "DEBUG" instead, so a development build is never mistaken for a
        // shipped release.
#if DEBUG
        const string build = "DEBUG";
#else
        var build = GetLocalBuildTime().ToString("HH:mm:ss");
#endif
        Text = $"Telltale D3DMesh Editor - v{UpdateChecker.CurrentVersion} (build: {build})";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(920, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;

        var preferences = AppPreferences.Load();
        GameConfig.Current = GameConfig.FromId(preferences.LastGame);
        _exportFormat = preferences.OutputFormat.Equals("GltfSeparate", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.GltfSeparate
            : ExportFormat.Glb;
        _btnDiffuseAtlas.Checked = preferences.TextureAtlas;
        _uncompressedTextures = preferences.UncompressedTextures;

        BuildUi();
        WireEvents();
        UpdateFormatButton();
        SetReadyState();

        // Quietly check GitHub for a newer release once the window is up. It only notifies when an
        // update exists and never installs anything by itself; failures (offline, etc.) are ignored.
        Shown += (_, _) => EnsureTreePanelWidth();
        Shown += async (_, _) => await CheckForUpdatesAsync(silent: true);
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
            _btnReimportSelected,
            new ToolStripSeparator(),
            _gameSelector,
            new ToolStripSeparator(),
            _btnCredits,
            _btnSettings,
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
        _btnCheckUpdates.Alignment = ToolStripItemAlignment.Right;
        _btnCheckUpdates.ToolTipText = "Checks GitHub for a newer version and shows the changelog. Nothing is installed automatically.";
        _btnSettings.ToolTipText = "Open tool settings.";
        _btnPan.CheckOnClick = true;
        _btnPose.CheckOnClick = true;
        _btnDiffuseAtlas.CheckOnClick = true;
        _btnCombineParts.CheckOnClick = true;
        _btnOpenArchive.ToolTipText = "Opens one or more Telltale .ttarch/.ttarch2 game containers and extracts their .d3dmesh, .d3dtx and .skl files automatically, then loads them. Select a mesh + texture archive together to see models with their textures.";
        _btnPan.ToolTipText = "Left-drag pans. Middle/Shift+drag also pans; mouse wheel zooms; F centers.";
        _btnPose.ToolTipText = "Select and drag skeleton joints; weighted mesh vertices deform in the preview.";
        _btnCombineParts.ToolTipText = "Shows detected combined model parts. Turn off to list every .d3dmesh separately.";
        _btnReimportSelected.ToolTipText = "Reimports an edited GLB/GLTF into the selected .d3dmesh, or splits a selected Combined model back into its original parts.";
        _btnDiffuseAtlas.ToolTipText = "When reimporting, packs diffuse/baseColor textures into one texture atlas and rewrites UV0 so the target can keep a single original diffuse texture name.";
        _btnCredits.ToolTipText = "Show credits.";

        _miFaces.Checked = true;
        _miPolygons.Checked = false;
        _miSkeleton.Checked = false;
        _btnView.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miFit,
            new ToolStripSeparator(),
            _miFaces,
            _miPolygons,
            _miSkeleton
        });

        _gameSelector.ToolTipText = "Selects which Telltale game's texture/model rules to apply. The Wolf Among Us behaves exactly as before.";
        foreach (var game in GameConfig.All)
        {
            var item = new ToolStripMenuItem(game.DisplayName) { Tag = game };
            item.Click += async (_, _) => await SelectGameAsync(game);
            _gameSelector.DropDownItems.Add(item);
        }

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
        _progress.Size = new Size(160, 14);
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        _statusStrip.Items.Add(_progressLabel);
        _statusStrip.Items.Add(_progress);
        _statusStrip.Items.Add(_detailLabel);

        Controls.Add(_split);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);
    }

    private Control CreateTreePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _searchText.Dock = DockStyle.Top;
        _searchText.PlaceholderText = "Search files...";
        _searchText.Margin = new Padding(0, 0, 6, 6);
        _btnReload.Dock = DockStyle.Top;
        _btnReload.Height = _searchText.PreferredHeight;
        _btnReload.Margin = new Padding(0, 0, 0, 6);
        panel.Controls.Add(_searchText, 0, 0);
        panel.Controls.Add(_btnReload, 1, 0);
        panel.Controls.Add(_tree, 0, 1);
        panel.SetColumnSpan(_tree, 2);
        return panel;
    }

    private void WireEvents()
    {
        _btnOpen.Click += async (_, _) => await OpenFolderDialogAsync();
        _btnOpenArchive.Click += async (_, _) => await OpenArchiveAsync();
        _btnReload.Click += async (_, _) =>
        {
            if (_rootFolder is not null)
            {
                await LoadFolderAsync(_rootFolder);
            }
        };
        _btnExtractSelected.Click += async (_, _) => await ExtractSelectedAsync();
        _btnExtractAll.Click += async (_, _) => await ExtractAllAsync();
        _btnReimportSelected.Click += async (_, _) => await ReimportSelectedAsync();
        _btnCredits.Click += (_, _) => ShowCreditsDialog();
        _btnCheckUpdates.Click += async (_, _) => await CheckForUpdatesAsync(silent: false);
        _btnSettings.Click += (_, _) => ShowSettingsDialog();
        _btnCombineParts.CheckedChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildAssetTree();
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
        _miFaces.Click += (_, _) =>
        {
            _miFaces.Checked = !_miFaces.Checked;
            _preview.ToggleFaces();
        };
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
        _tree.AfterSelect += (_, e) => HandleTreeAfterSelect(e.Node);
        _tree.NodeMouseClick += (_, e) => HandleTreeNodeMouseClick(e);
        _tree.AfterExpand += (_, _) => AutoFitTreeWidth();
        _tree.AfterCollapse += (_, _) => AutoFitTreeWidth();
        _searchDebounceTimer.Interval = 220;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildAssetTree();
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

        if (!EnsureGameSelectedForOpen())
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
            await LoadFolderAsync(Path.GetDirectoryName(path)!);
        }
    }

    private void UpdateFormatButton()
    {
        var format = _exportFormat == ExportFormat.Glb ? "GLB" : "GLTF";
        var atlas = _btnDiffuseAtlas.Checked ? "on" : "off";
        _btnSettings.ToolTipText = $"Open tool settings. Output Format: {format}; Texture Atlas: {atlas}.";
    }

    private void ShowSettingsDialog()
    {
        using var dialog = new Form
        {
            Text = "Settings",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 196),
            Font = Font,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var formatLabel = new Label
        {
            Text = "Output Format:",
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
            Text = "Texture Atlas:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var atlasCheck = new CheckBox
        {
            Text = "Generate texture atlas",
            Checked = _btnDiffuseAtlas.Checked,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };

        var compressionLabel = new Label
        {
            Text = "Textures:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 8, 10),
        };
        var uncompressedCheck = new CheckBox
        {
            Text = "Uncompressed textures (no DXT)",
            Checked = _uncompressedTextures,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 10),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Margin = new Padding(0),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 86 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 86 };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
#if DEBUG
        // Debug-only: pull the latest release and show the update dialog as if an update had arrived, so
        // the update UI (HTML rendering, buttons) can be tested without publishing a newer version.
        var simulateUpdate = new Button { Text = "Simulate update", AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
        simulateUpdate.Click += async (_, _) => await SimulateUpdateAsync();
        buttons.Controls.Add(simulateUpdate);
#endif

        layout.Controls.Add(formatLabel, 0, 0);
        layout.Controls.Add(formatCombo, 1, 0);
        layout.Controls.Add(atlasLabel, 0, 1);
        layout.Controls.Add(atlasCheck, 1, 1);
        layout.Controls.Add(compressionLabel, 0, 2);
        layout.Controls.Add(uncompressedCheck, 1, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _exportFormat = formatCombo.SelectedIndex == 0 ? ExportFormat.Glb : ExportFormat.GltfSeparate;
        _btnDiffuseAtlas.Checked = atlasCheck.Checked;
        _uncompressedTextures = uncompressedCheck.Checked;
        UpdateFormatButton();
        AppPreferences.SaveToolSettings(
            _exportFormat == ExportFormat.Glb ? "Glb" : "GltfSeparate",
            _btnDiffuseAtlas.Checked,
            _uncompressedTextures);
    }

    private void SetReadyState()
    {
        _btnExtractSelected.Enabled = false;
        _btnExtractAll.Enabled = false;
        _btnReimportSelected.Enabled = false;
        _btnCombineParts.Enabled = false;
        _btnReload.Enabled = false;
        _searchText.Enabled = false;
        _statusLabel.Text = "Open a folder containing .d3dmesh, .skl and .d3dtx files to begin.";
        _detailLabel.Text = "";
    }

    private static string BuildLoadedDetailText(int modelCount, int groupCount)
    {
        var models = modelCount == 1 ? "1 model" : $"{modelCount} models";
        var groups = groupCount == 1 ? "1 combined group" : $"{groupCount} combined groups";
        return $"{models} | {groups}";
    }

    private async Task OpenFolderDialogAsync()
    {
        if (!EnsureGameSelectedForOpen())
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder containing the .d3dmesh, .d3dtx and .skl files",
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
        if (!EnsureGameSelectedForOpen())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Select one or more Telltale archives (.ttarch / .ttarch2)",
            Filter = "Telltale archives (*.ttarch;*.ttarch2)|*.ttarch;*.ttarch2|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var archivePaths = dialog.FileNames;
        var baseDir = Path.GetDirectoryName(archivePaths[0]) ?? Environment.CurrentDirectory;

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
            _statusLabel.Text = archivePaths.Length == 1
                ? "Extracting archive..."
                : $"Extracting {archivePaths.Length} archives...";

            await Task.Run(() =>
            {
                foreach (var archivePath in archivePaths)
                {
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
                }
            });

            if (totalExtracted > 0)
            {
                var loadProgress = new Progress<double>(fraction =>
                    SetProgress((int)(fraction * 1000), 1000, $"Reading folder... {(int)(fraction * 100)}%"));
                await ReloadAssetsAsync(loadFolder, loadProgress);
            }

            var detected = detectedGames.Count > 0
                ? $"\n\nDetected: {string.Join(", ", detectedGames)}"
                : "";
            var message = totalExtracted > 0
                ? $"Extracted {totalExtracted} asset file(s) from {archivePaths.Length} archive(s)\n\ninto:\n{loadFolder}{detected}"
                : "No .d3dmesh, .d3dtx or .skl files were found in the selected archive(s).";

            if (failures.Count > 0)
            {
                message += $"\n\nCould not open {failures.Count} archive(s):\n" + string.Join("\n", failures);
            }

            return message;
        });
    }

    private void UpdateGameSelector()
    {
        _gameSelector.Text = $"Game: {GameConfig.Current.DisplayName}";
        foreach (var item in _gameSelector.DropDownItems.OfType<ToolStripMenuItem>())
        {
            item.Checked = ReferenceEquals(item.Tag, GameConfig.Current);
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

        if (_rootFolder is not null)
        {
            await LoadFolderAsync(_rootFolder);
        }
    }

    private bool EnsureGameSelectedForOpen()
    {
        if (GameConfig.Current.Id != GameId.Generic)
        {
            return true;
        }

        using var dialog = new GameSelectionDialog(GameConfig.Current);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        GameConfig.Current = dialog.SelectedGame;
        AppPreferences.SaveGameConfig(GameConfig.Current);
        UpdateGameSelector();
        return true;
    }

    // Entry point for Open Folder / Reload / drag-drop. Owns the busy state, the progress bar and error
    // reporting so a large folder loads on a background thread without freezing the window.
    private async Task LoadFolderAsync(string folder)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<double>(fraction =>
                SetProgress((int)(fraction * 1000), 1000, $"Reading folder... {(int)(fraction * 100)}%"));
            await ReloadAssetsAsync(folder, progress);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Error.";
            var logPath = ErrorLog.Write(ex, "Folder load failed");
            MessageBox.Show(
                $"Could not load the folder. A detailed log was written to:\n{logPath}\n\n{ex.Message}",
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

        var (assets, groups) = await Task.Run(() =>
        {
            var discovered = ModelAsset.Discover(folder, SubRange(progress, 0.0, 0.7));
            var grouped = ModelAssetGroup.Discover(discovered, folder, SubRange(progress, 0.7, 1.0));
            return (discovered, grouped);
        });

        _assets = assets;
        _assetGroups = groups;
        _searchText.Enabled = true;
        _searchDebounceTimer.Stop();
        if (_searchText.TextLength > 0)
        {
            _searchText.Clear();
            _searchDebounceTimer.Stop();
        }

        RebuildAssetTree();

        _btnReload.Enabled = true;
        _btnExtractAll.Enabled = _assets.Count > 0;
        _btnCombineParts.Enabled = _assets.Count > 0;
        _btnExtractSelected.Enabled = false;
        _btnReimportSelected.Enabled = false;
        _detailLabel.Text = BuildLoadedDetailText(_assets.Count, _btnCombineParts.Checked ? _assetGroups.Count : 0);
        _statusLabel.Text = _assets.Count == 0
            ? "No .d3dmesh files were found in this folder."
            : $"Loaded: {folder}";
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

    private void RebuildAssetTree()
    {
        if (_rootFolder is null)
        {
            return;
        }

        var query = _searchText.Text.Trim();
        var visibleAssets = string.IsNullOrEmpty(query)
            ? _assets
            : _assets
                .Where(asset => asset.Matches(_rootFolder, query))
                .ToList();
        var visibleGroups = _btnCombineParts.Checked
            ? string.IsNullOrEmpty(query)
                ? _assetGroups
                : _assetGroups
                    .Where(group => group.Matches(_rootFolder, query))
                    .ToList()
            : [];
        if (_btnCombineParts.Checked)
        {
            visibleAssets = FilterLooseAssetsForTree(visibleAssets, visibleGroups).ToList();
        }

        ClearTreeSelectionState();
        _selectedAsset = null;
        _selectedGroup = null;
        _btnExtractSelected.Enabled = false;
        _btnReimportSelected.Enabled = false;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var root = new TreeNode(Path.GetFileName(_rootFolder.TrimEnd('\\', '/')))
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
        _tree.EndUpdate();

        _detailLabel.Text = string.IsNullOrEmpty(query)
            ? BuildLoadedDetailText(_assets.Count, visibleGroups.Count)
            : $"{visibleAssets.Count} of {_assets.Count} models | {visibleGroups.Count} of {_assetGroups.Count} groups";
        if ((_assets.Count > 0 || _assetGroups.Count > 0) && visibleAssets.Count == 0 && visibleGroups.Count == 0)
        {
            _statusLabel.Text = $"No files match \"{query}\".";
        }
        else if (!string.IsNullOrEmpty(query))
        {
            _statusLabel.Text = $"Search: \"{query}\"";
        }
        else
        {
            _statusLabel.Text = _assets.Count == 0
                ? "No .d3dmesh files were found in this folder."
                : $"Loaded: {_rootFolder}";
        }

        AutoFitTreeWidth();
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

    private void OnTreeSelect(TreeNode? node)
    {
        if (node?.Tag is ModelAssetGroup group)
        {
            _selectedAsset = null;
            _selectedGroup = group;
            _btnExtractSelected.Enabled = true;
            _btnReimportSelected.Enabled = true;
            PreviewAssetGroup(group);
            return;
        }

        if (node?.Tag is not ModelAsset asset)
        {
            _selectedAsset = null;
            _selectedGroup = null;
            _btnExtractSelected.Enabled = false;
            _btnReimportSelected.Enabled = false;
            _detailLabel.Text = BuildLoadedDetailText(_assets.Count, _assetGroups.Count);
            return;
        }

        _selectedAsset = asset;
        _selectedGroup = null;
        _btnExtractSelected.Enabled = true;
        _btnReimportSelected.Enabled = true;
        PreviewAsset(asset);
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
        _btnReimportSelected.Enabled = GetSingleSelectedAssetForReimport() is not null ||
                                      GetSingleSelectedGroupForReimport() is not null;
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
        try
        {
            var mesh = D3DMeshParser.Parse(File.ReadAllBytes(asset.MeshPath));
            SkeletonData? skeleton = null;
            if (asset.SkeletonPath is not null)
            {
                skeleton = SkeletonLoader.Load(asset.SkeletonPath, version: 13);
            }

            var textures = _rootFolder is null
                ? new Dictionary<int, MaterialTextureSet>()
                : TextureResolver.ResolveForMesh(_rootFolder, asset.MeshPath, mesh);
            _preview.SetScene(mesh, skeleton, textures);
            _statusLabel.Text = "Preview ready.";
            var textureCount = textures.Values.Sum(set => set.Count);
            _detailLabel.Text = $"textures: {textureCount}";
        }
        catch (Exception ex)
        {
            _preview.SetScene(null, null);
            _statusLabel.Text = $"Preview error: {Path.GetFileName(asset.MeshPath)}";
            _detailLabel.Text = ex.Message;
        }
    }

    private void PreviewAssetGroup(ModelAssetGroup group)
    {
        try
        {
            if (_rootFolder is null)
            {
                return;
            }

            var previewAsset = ExtractionService.BuildPreviewAsset(group, _rootFolder);
            _preview.SetScene(previewAsset.Mesh, previewAsset.Skeleton, previewAsset.Textures);
            _statusLabel.Text = "Combined preview ready.";
            var textureCount = previewAsset.Textures.Values.Sum(set => set.Count);
            _detailLabel.Text = $"parts: {group.Assets.Count} | textures: {textureCount}";
        }
        catch (Exception ex)
        {
            _preview.SetScene(null, null);
            _statusLabel.Text = $"Preview error: {group}";
            _detailLabel.Text = ex.Message;
        }
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
            SetProgress(p.Done, selectedCount, $"Extracting {p.Done}/{selectedCount}: {p.Name}"));
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
                        lines.Add($"OK combined group: {group.Name} -> {Path.GetFileName(outputPath)}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lines.Add($"FAIL combined group: {group.Name} -> {ex.Message}");
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
                        lines.Add($"OK model: {stem} -> {Path.GetFileName(outputPath)}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lines.Add($"FAIL model: {stem} -> {ex.Message}");
                    }
                }

                if (selectedCount == 1 && ok == 1 && failed == 0)
                {
                    return string.Join(Environment.NewLine, lines);
                }

                return string.Join(
                    Environment.NewLine,
                    $"Extracted selected items to: {output}",
                    $"OK: {ok}/{selectedCount}, failed: {failed}",
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
                _statusLabel.Text = line;
                SetProgress(++done, total, $"Extracting {Math.Min(done, total)}/{total}...");
            });
            var summary = await Task.Run(() => ExtractionService.ExtractAll(assets, root, output, format, progress));
            sw.Stop();
            return $"{summary}\nTime: {sw.Elapsed:g}";
        });
    }

    private async Task ReimportSelectedAsync()
    {
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
            var result = await Task.Run(() => ReimportSingleAsset(asset, input, output, useDiffuseAtlas, uncompressedTextures));
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

        await RunWithUiLockAsync(async () =>
        {
            var result = await Task.Run(() =>
            {
                var model = GltfReader.Load(input);
                return ReimportCombinedGroup(group, root, model, input, outputFolder, useDiffuseAtlas, uncompressedTextures);
            });

            return result;
        });

        if (group.Assets.Any(asset => Path.GetFullPath(outputFolder).Equals(Path.GetDirectoryName(Path.GetFullPath(asset.MeshPath)), StringComparison.OrdinalIgnoreCase)))
        {
            PreviewAssetGroup(group);
        }
    }

    private static string ReimportSingleAsset(ModelAsset asset, string input, string output, bool useDiffuseAtlas, bool uncompressedTextures)
    {
        var gameConfig = GameConfig.Current;
        var layout = D3DMeshLayout.Build(File.ReadAllBytes(asset.MeshPath));
        var model = GltfModelPreprocessor.ApplyGameReinsertRules(GltfReader.Load(input), gameConfig);
        // With the atlas active the face/hand ink-line textures are baked into the atlas and their detail
        // slots are dropped, so the normal detail-write inversion can't run. Invert the face and hand line
        // alpha here, before atlasing, so the baked head and hands still render correctly (TWD S2 shader).
        if (useDiffuseAtlas && gameConfig.InvertHeadLineAlphaOnReimport)
        {
            model = CharacterLineAtlasFix.InvertFaceAndHandLineAlpha(model);
        }

        var atlas = ApplyDiffuseAtlasIfRequested(model, useDiffuseAtlas);
        model = atlas.Model;
        var skeleton = LoadSkeletonOrNull(asset.SkeletonPath, layout.Version);
        var textureOptions = BuildReinsertTextureOptions(useDiffuseAtlas, uncompressedTextures);
        var textures = ReinsertTextureService.WriteAllReferencedTextures(model, asset.MeshPath, output, gameConfig, textureOptions);
        var bytes = MeshReinserter.ReinsertGeometry(layout, model, textures, skeleton, gameConfig);
        File.WriteAllBytes(output, bytes);

        var check = D3DMeshLayout.Build(bytes);
        var status = check.TailOffset + check.TailLength == bytes.Length
            ? "Output verified: layout closes at EOF."
            : "Warning: output was written, but the layout does not close at EOF.";
        var textureCount = textures.WrittenNames.Count;
        var textureLine = textureCount > 0
            ? $"\nTextures: {textureCount} .d3dtx file(s) written next to the output mesh."
            : "";
        var skeletonLine = RebuildSkeletonForReimport(asset, model, output);
        var atlasLine = BuildAtlasStatusLine(atlas);

        return $"Reimported: {Path.GetFileName(asset.MeshPath)}\nInput: {input}\nOutput: {output}{textureLine}{atlasLine}{skeletonLine}\n{status}";
    }

    // Rebuilds the .skl next to the reimported mesh from the skeleton inside the GLB. When the model
    // keeps the game's original skeleton, the rebuild merges the edits onto the original (so an
    // untouched skeleton stays byte-identical). Prop targets intentionally stay geometry-only: a
    // skinned GLB can be used as a static prop, but the target should not gain a brand-new .skl.
    // Returns a status line (empty when the GLB carries no skin or the target has no skeleton).
    private static string RebuildSkeletonForReimport(ModelAsset asset, GltfModel model, string output)
    {
        if (model.Skeleton is null || model.Skeleton.Bones.Count == 0)
        {
            if (asset.SkeletonPath is not null && File.Exists(asset.SkeletonPath))
            {
                return "\nSkeleton: kept the target .skl; imported model has no skin and was bound as static geometry.";
            }

            return "";
        }

        if (asset.SkeletonPath is null || !File.Exists(asset.SkeletonPath))
        {
            return "\nSkeleton: skipped because the target asset has no original .skl; imported rig was baked as static geometry.";
        }

        try
        {
            var outputDir = Path.GetDirectoryName(output) ?? "";
            var skeletonName = Path.GetFileName(asset.SkeletonPath);
            var skeletonOutput = Path.Combine(outputDir, skeletonName);

            var skeletonBytes = SkeletonRebuilder.RebuildWithEdits(asset.SkeletonPath, model.Skeleton);
            File.WriteAllBytes(skeletonOutput, skeletonBytes);
            return $"\nSkeleton: {skeletonName} rebuilt from the original skeleton + your edits ({model.Skeleton.Bones.Count} bones).";
        }
        catch (Exception ex)
        {
            return $"\nSkeleton: could not rebuild the .skl ({ex.Message}).";
        }
    }

    private static string ReimportCombinedGroup(
        ModelAssetGroup group,
        string inputRoot,
        GltfModel combinedModel,
        string input,
        string outputFolder,
        bool useDiffuseAtlas,
        bool uncompressedTextures)
    {
        Directory.CreateDirectory(outputFolder);
        var gameConfig = GameConfig.Current;
        combinedModel = GltfModelPreprocessor.ApplyGameReinsertRules(combinedModel, gameConfig);
        var sourcePrimitives = BuildCombinedSourcePrimitiveMap(combinedModel, inputRoot);
        var ok = 0;
        var skipped = 0;
        var totalTextures = 0;
        var lines = new List<string>();

        foreach (var asset in group.Assets)
        {
            var fullMeshPath = Path.GetFullPath(asset.MeshPath);
            if (!sourcePrimitives.TryGetValue(fullMeshPath, out var primitives) || primitives.Count == 0)
            {
                skipped++;
                lines.Add($"SKIP {Path.GetFileName(asset.MeshPath)}: no edited primitives with matching combined source metadata.");
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
            if (useDiffuseAtlas && gameConfig.InvertHeadLineAlphaOnReimport)
            {
                partModel = CharacterLineAtlasFix.InvertFaceAndHandLineAlpha(partModel);
            }

            var atlas = ApplyDiffuseAtlasIfRequested(partModel, useDiffuseAtlas);
            partModel = atlas.Model;
            var output = Path.Combine(outputFolder, Path.GetFileName(asset.MeshPath));
            var layout = D3DMeshLayout.Build(File.ReadAllBytes(asset.MeshPath));
            var skeleton = LoadSkeletonOrNull(asset.SkeletonPath, layout.Version);
            var textureOptions = BuildReinsertTextureOptions(useDiffuseAtlas, uncompressedTextures);
            var textures = ReinsertTextureService.WriteAllReferencedTextures(partModel, asset.MeshPath, output, gameConfig, textureOptions);
            var bytes = MeshReinserter.ReinsertGeometry(layout, partModel, textures, skeleton, gameConfig);
            File.WriteAllBytes(output, bytes);

            var check = D3DMeshLayout.Build(bytes);
            var status = check.TailOffset + check.TailLength == bytes.Length ? "verified" : "layout warning";
            totalTextures += textures.WrittenNames.Count;
            ok++;
            var atlasSummary = atlas.Applied
                ? $", atlas {atlas.SourceTextureCount}->{atlas.AtlasWidth}x{atlas.AtlasHeight}"
                : "";
            lines.Add($"OK {Path.GetFileName(asset.MeshPath)}: {primitives.Count} primitive(s), {textures.WrittenNames.Count} texture(s){atlasSummary}, {status}.");
        }

        if (ok == 0)
        {
            throw new InvalidOperationException(
                "The selected GLB/GLTF does not contain combined source metadata for this group. " +
                "Extract the Combined model with this tool and keep the primitive extras/source data when editing/exporting.");
        }

        return string.Join(
            Environment.NewLine,
            $"Reimported combined group: {group.Name}",
            $"Input: {input}",
            $"Output folder: {outputFolder}",
            $"Parts OK: {ok}/{group.Assets.Count}, skipped: {skipped}, textures written: {totalTextures}",
            string.Join(Environment.NewLine, lines));
    }

    private static ReinsertTextureOptions BuildReinsertTextureOptions(bool useDiffuseAtlas, bool uncompressedTextures)
        => useDiffuseAtlas
            ? new ReinsertTextureOptions
            {
                ForceUncompressed = uncompressedTextures,
                IncludedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "diffuse",
                    "detail_diffuse",
                    "tex7",
                    "tex8",
                    "bump",
                    "normal",
                },
            }
            : new ReinsertTextureOptions { ForceUncompressed = uncompressedTextures };

    private static GltfDiffuseAtlasResult ApplyDiffuseAtlasIfRequested(GltfModel model, bool useDiffuseAtlas)
        => useDiffuseAtlas
            ? GltfDiffuseAtlasPacker.Pack(model)
            : new GltfDiffuseAtlasResult(model, Applied: false, SourceTextureCount: 0, AtlasWidth: 0, AtlasHeight: 0, AtlasName: "", Warnings: []);

    private static string BuildAtlasStatusLine(GltfDiffuseAtlasResult atlas)
    {
        if (!atlas.Applied)
        {
            return atlas.SourceTextureCount == 1
                ? "\nTexture Atlas: skipped because the model already uses one diffuse texture."
                : "";
        }

        var line = $"\nTexture Atlas: packed {atlas.SourceTextureCount} texture region(s) into one {atlas.AtlasWidth}x{atlas.AtlasHeight} atlas.";
        if (atlas.Warnings.Count > 0)
        {
            line += "\nAtlas warnings: " + string.Join(" ", atlas.Warnings.Take(3));
        }

        return line;
    }

    private static Dictionary<string, List<GltfPrimitive>> BuildCombinedSourcePrimitiveMap(GltfModel model, string inputRoot)
    {
        var result = new Dictionary<string, List<GltfPrimitive>>(StringComparer.OrdinalIgnoreCase);
        var missingSource = model.Primitives.Count(primitive => string.IsNullOrWhiteSpace(primitive.SourceMeshPath));
        if (missingSource > 0)
        {
            throw new InvalidOperationException(
                $"{missingSource} primitive(s) do not have combined source metadata. " +
                "Make sure the edited file was originally extracted as a Combined model and that the exporter preserved custom extras.");
        }

        foreach (var primitive in model.Primitives)
        {
            var source = primitive.SourceMeshPath!;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(inputRoot, source));
            if (!result.TryGetValue(fullPath, out var primitives))
            {
                primitives = [];
                result[fullPath] = primitives;
            }

            primitives.Add(primitive);
        }

        return result;
    }

    private string? ChooseOutputFolder()
    {
        var selectedPath = SuggestedOutputFolder();
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the extraction output folder",
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
            Title = "Select the edited GLB/GLTF to reimport",
            Filter = "glTF model (*.glb;*.gltf)|*.glb;*.gltf|All files (*.*)|*.*",
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
            Description = "Select the folder where the reimported files will be written",
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
            _statusLabel.Text = "Done.";
            MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Error.";
            var logPath = ErrorLog.Write(ex, "Operation failed");
            MessageBox.Show(
                $"Operation failed. A detailed log was written to:\n{logPath}\n\n{ex.Message}",
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
        _btnReload.Enabled = !busy && _rootFolder is not null;
        _btnExtractAll.Enabled = !busy && _rootFolder is not null && _assets.Count > 0;
        _btnExtractSelected.Enabled = !busy && _rootFolder is not null && HasExtractSelection();
        _btnReimportSelected.Enabled = !busy &&
                                      (GetSingleSelectedAssetForReimport() is not null ||
                                       GetSingleSelectedGroupForReimport() is not null);
        _btnCombineParts.Enabled = !busy && _rootFolder is not null && _assets.Count > 0;
        _btnPan.Enabled = !busy;
        _btnPose.Enabled = !busy;
        _btnView.Enabled = !busy;
        _btnCredits.Enabled = !busy;
        _btnCheckUpdates.Enabled = !busy;
        _btnSettings.Enabled = !busy;
        _gameSelector.Enabled = !busy;
        _searchText.Enabled = !busy && _rootFolder is not null;

        if (busy)
        {
            // Start as an indeterminate "working" animation; long operations switch it to a real
            // percentage via SetProgress. This makes it obvious the tool is busy, not frozen.
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            _progressLabel.Text = "Working...";
            _progress.Visible = true;
            _progressLabel.Visible = true;
        }
        else
        {
            _progress.Visible = false;
            _progressLabel.Visible = false;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Style = ProgressBarStyle.Blocks;
        }
    }

    // Switches the toolbar bar to a real percentage. Safe to call from a Progress<T> callback (UI thread).
    private void SetProgress(int done, int total, string label)
    {
        if (total <= 0)
        {
            return;
        }

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = total;
        _progress.Value = Math.Clamp(done, 0, total);
        _progressLabel.Text = label;
    }

    // Looks for a newer GitHub release. When silent (startup), stays quiet unless an update is found;
    // when triggered from the menu, also confirms when the tool is already up to date.
    private async Task CheckForUpdatesAsync(bool silent)
    {
        var info = await UpdateChecker.CheckForUpdateAsync();
        if (info is null)
        {
            if (!silent)
            {
                MessageBox.Show(
                    $"You're on the latest version (v{UpdateChecker.CurrentVersion}).",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        ShowUpdateDialog(info);
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
                "Could not fetch the latest release to simulate (offline or rate-limited?).",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ShowUpdateDialog(info);
    }
#endif

    // Shows the new version and its changelog, and lets the user download it or open the release page.
    // Deliberately never downloads or replaces files on its own — the user stays in control.
    private void ShowUpdateDialog(UpdateInfo info)
    {
        using var dialog = new Form
        {
            Text = "Update available",
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
            Text = $"A new version is available: {info.Title}",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };

        var subtitle = new Label
        {
            Text = $"You have v{UpdateChecker.CurrentVersion}; the latest is v{info.Version}. "
                 + "Nothing is installed automatically — you choose what to do.",
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
            if (changelog.DocumentText.Length > 0 && e.Url.Scheme is "http" or "https")
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
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(6, 0, 0, 0) };
        var viewOnGitHub = new Button { Text = "View on GitHub", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var download = new Button { Text = "Download", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        viewOnGitHub.Click += (_, _) => OpenUrl(info.ReleaseUrl);
        download.Click += (_, _) => OpenUrl(string.IsNullOrEmpty(info.DownloadUrl) ? info.ReleaseUrl : info.DownloadUrl!);
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

    private void ShowCreditsDialog()
    {
        using var dialog = new Form
        {
            Text = "Credits",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 350),
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

        var title = new Label
        {
            Text = "Telltale D3DMesh Editor",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };

        var madeBy = new Label
        {
            Text = "Made by Heitor Spectre.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };

        var thanks = new Label
        {
            Text = "Special thanks to:",
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

        var paragraph = new TextBox
        {
            Text = "Without their analysis and the documentation available in their repositories, none of this would have happened. I would like to thank them for supporting the community, creating tools, and documenting the formats. Their work made it possible to build an editor capable of both extracting assets from the game and reinserting them back into it.",
            ReadOnly = true,
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = dialog.BackColor,
            TabStop = false,
            Width = 500,
            Height = 82,
            Margin = new Padding(0, 0, 0, 10),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        buttons.Controls.Add(ok);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(madeBy, 0, 1);
        layout.Controls.Add(thanks, 0, 2);
        layout.Controls.Add(links, 0, 3);
        layout.Controls.Add(paragraph, 0, 4);
        layout.Controls.Add(buttons, 0, 5);

        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.ShowDialog(this);
    }

    private static void AddCreditLink(FlowLayoutPanel links, string name, string url)
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
        links.Controls.Add(link);
    }

    private void AutoFitTreeWidth()
    {
        if (_tree.Nodes.Count == 0)
        {
            EnsureTreePanelWidth();
            return;
        }

        var max = 0;
        using (var graphics = _tree.CreateGraphics())
        {
            MeasureVisibleNodes(_tree.Nodes, graphics, _tree.Font, 0, ref max);
        }

        var target = max + SystemInformation.VerticalScrollBarWidth + 42;
        var minWidth = MinimumTreePanelWidth;
        var maxWidth = Math.Max(minWidth, ClientSize.Width / 2);
        target = Math.Clamp(target, minWidth, maxWidth);
        if (Math.Abs(_split.SplitterDistance - target) > 4)
        {
            _split.SplitterDistance = target;
        }
    }

    private void EnsureTreePanelWidth()
    {
        if (_split.Width <= 0)
        {
            return;
        }

        var maxWidth = Math.Max(MinimumTreePanelWidth, _split.Width - _split.Panel2MinSize - _split.SplitterWidth);
        var target = Math.Clamp(PreferredTreePanelWidth, MinimumTreePanelWidth, maxWidth);
        if (_split.SplitterDistance < target)
        {
            _split.SplitterDistance = target;
        }
    }

    private static void MeasureVisibleNodes(TreeNodeCollection nodes, Graphics graphics, Font font, int depth, ref int maxPx)
    {
        foreach (TreeNode node in nodes)
        {
            var width = (int)graphics.MeasureString(node.Text, font).Width + depth * 19;
            if (width > maxPx)
            {
                maxPx = width;
            }

            if (node.IsExpanded)
            {
                MeasureVisibleNodes(node.Nodes, graphics, font, depth + 1, ref maxPx);
            }
        }
    }
}
