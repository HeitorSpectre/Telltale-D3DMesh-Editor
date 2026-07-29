using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using TelltaleD3DMeshEditor.Core;
using TelltaleD3DMeshEditor.Viewer;

namespace TelltaleD3DMeshEditor.UI;

internal static class UiTheme
{
    // Neutral layered palette close to the dark themes used by desktop IDEs and 3D editors. Surfaces
    // differ just enough to keep hierarchy visible without creating black/white contrast cliffs.
    private static readonly Color DarkWindow = Color.FromArgb(32, 33, 36);
    private static readonly Color DarkSurface = Color.FromArgb(42, 43, 46);
    private static readonly Color DarkInput = Color.FromArgb(51, 52, 56);
    private static readonly Color DarkButton = Color.FromArgb(56, 57, 61);
    private static readonly Color DarkBorder = Color.FromArgb(67, 69, 74);
    private static readonly Color DarkText = Color.FromArgb(222, 224, 228);
    private static readonly Color DarkMutedText = Color.FromArgb(166, 169, 175);
    private static readonly Color Accent = Color.FromArgb(0, 122, 204);
    private static readonly ConditionalWeakTable<Control, OriginalColors> OriginalColorsByControl = new();

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Apply(Control root, AppTheme theme)
    {
        Current = theme;
        CaptureOriginalColors(root);
        ApplyControl(root, theme);

        if (root is Form form)
        {
            ApplyTitleBar(form, theme);
            form.HandleCreated -= FormHandleCreated;
            form.HandleCreated += FormHandleCreated;
        }
    }

    private static void CaptureOriginalColors(Control control)
    {
        _ = OriginalColorsByControl.GetValue(
            control,
            static item => new OriginalColors(item.BackColor, item.ForeColor));
        foreach (Control child in control.Controls)
        {
            CaptureOriginalColors(child);
        }
    }

    public static void Apply(ContextMenuStrip menu, AppTheme theme)
    {
        Current = theme;
        ApplyToolStrip(menu, theme);
    }

    private static void FormHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form)
        {
            ApplyTitleBar(form, Current);
        }
    }

    private static void ApplyControl(Control control, AppTheme theme)
    {
        var original = OriginalColorsByControl.GetValue(
            control,
            static item => new OriginalColors(item.BackColor, item.ForeColor));
        var dark = theme == AppTheme.Dark;
        var windowBack = dark ? DarkWindow : SystemColors.Control;
        var surfaceBack = dark ? DarkSurface : SystemColors.Window;
        var inputBack = dark ? DarkInput : SystemColors.Window;
        var text = dark ? DarkText : SystemColors.ControlText;

        switch (control)
        {
            case MeshPreviewControl preview:
                preview.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.FromArgb(122, 122, 120);
                preview.ForeColor = dark ? DarkText : Color.Gainsboro;
                break;

            case ToolStrip strip:
                ApplyToolStrip(strip, theme);
                break;

            case TextBoxBase textBox:
                textBox.BackColor = inputBack;
                textBox.ForeColor = text;
                break;

            case ComboBox combo:
                combo.BackColor = inputBack;
                combo.ForeColor = text;
                ApplyComboBox(combo, theme);
                break;

            case TreeView tree:
                tree.BackColor = surfaceBack;
                tree.ForeColor = text;
                tree.LineColor = dark ? DarkMutedText : SystemColors.GrayText;
                break;

            case ListBox list:
                list.BackColor = surfaceBack;
                list.ForeColor = text;
                break;

            case Button button:
                ApplyButton(button, theme);
                break;

            case CheckBox checkBox:
                ApplyCheckBox(checkBox, theme);
                break;

            case TabControl tabs:
                ApplyTabs(tabs, theme);
                tabs.BackColor = windowBack;
                tabs.ForeColor = text;
                break;

            case TabPage page:
                page.UseVisualStyleBackColor = !dark;
                page.BackColor = windowBack;
                page.ForeColor = text;
                break;

            case LinkLabel link:
                link.BackColor = windowBack;
                link.ForeColor = text;
                link.LinkColor = dark ? Color.FromArgb(86, 156, 214) : Color.Blue;
                link.ActiveLinkColor = dark ? Color.FromArgb(117, 190, 255) : Color.Red;
                link.VisitedLinkColor = dark ? Color.FromArgb(180, 130, 220) : Color.Purple;
                break;

            default:
                control.BackColor = HasCustomBackColor(original.BackColor) ? original.BackColor : windowBack;
                control.ForeColor = HasCustomForeColor(original.ForeColor) ? original.ForeColor : text;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, theme);
        }
    }

    private static void ApplyButton(Button button, AppTheme theme)
    {
        var original = OriginalColorsByControl.GetValue(
            button,
            static item => new OriginalColors(item.BackColor, item.ForeColor));
        var hasCustomColor = HasCustomBackColor(original.BackColor);
        button.Paint -= PaintDisabledButton;
        if (theme == AppTheme.Dark)
        {
            // Preserve intentionally branded buttons (Discord, community header actions, etc.).
            if (!hasCustomColor)
            {
                button.BackColor = DarkButton;
            }
            else
            {
                button.BackColor = original.BackColor;
            }

            button.ForeColor = HasCustomForeColor(original.ForeColor) ? original.ForeColor : DarkText;
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = DarkBorder;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(66, 68, 73);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 75, 80);
            button.Paint += PaintDisabledButton;
            return;
        }

        button.BackColor = original.BackColor;
        button.ForeColor = original.ForeColor;
        button.UseVisualStyleBackColor = true;
        button.FlatStyle = FlatStyle.Standard;
    }

    private static void ApplyComboBox(ComboBox combo, AppTheme theme)
    {
        combo.DrawItem -= DrawComboBoxItem;
        if (theme == AppTheme.Dark)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += DrawComboBoxItem;
            return;
        }

        combo.FlatStyle = FlatStyle.Standard;
        combo.DrawMode = DrawMode.Normal;
    }

    private static void DrawComboBoxItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        var isEditArea = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var selected = !isEditArea && (e.State & DrawItemState.Selected) != 0;
        var backgroundColor = selected ? Accent : DarkInput;
        var foregroundColor = selected ? Color.White : DarkText;
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index >= 0 && e.Index < combo.Items.Count)
        {
            var text = combo.GetItemText(combo.Items[e.Index]);
            var textBounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(
                e.Graphics,
                text,
                combo.Font,
                textBounds,
                foregroundColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        if ((e.State & DrawItemState.Focus) != 0 && !isEditArea)
        {
            e.DrawFocusRectangle();
        }
    }

    private static void ApplyCheckBox(CheckBox checkBox, AppTheme theme)
    {
        checkBox.Paint -= PaintDarkCheckBox;
        if (theme == AppTheme.Dark)
        {
            checkBox.UseVisualStyleBackColor = false;
            checkBox.Paint += PaintDarkCheckBox;
        }
        else
        {
            checkBox.UseVisualStyleBackColor = true;
        }
    }

    private static void PaintDarkCheckBox(object? sender, PaintEventArgs e)
    {
        if (sender is not CheckBox checkBox || Current != AppTheme.Dark)
        {
            return;
        }

        e.Graphics.Clear(checkBox.BackColor);
        const int boxSize = 14;
        var box = new Rectangle(0, Math.Max(0, (checkBox.ClientSize.Height - boxSize) / 2), boxSize, boxSize);
        using var boxBackground = new SolidBrush(DarkInput);
        using var border = new Pen(checkBox.Enabled ? DarkBorder : Color.FromArgb(57, 59, 63));
        e.Graphics.FillRectangle(boxBackground, box);
        e.Graphics.DrawRectangle(border, box);

        if (checkBox.Checked)
        {
            using var checkPen = new Pen(Color.White, 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            e.Graphics.DrawLines(
                checkPen,
                [
                    new Point(box.Left + 3, box.Top + 7),
                    new Point(box.Left + 6, box.Top + 10),
                    new Point(box.Left + 11, box.Top + 4),
                ]);
        }

        var textBounds = new Rectangle(
            box.Right + 7,
            0,
            Math.Max(0, checkBox.ClientSize.Width - box.Right - 7),
            checkBox.ClientSize.Height);
        TextRenderer.DrawText(
            e.Graphics,
            checkBox.Text,
            checkBox.Font,
            textBounds,
            checkBox.Enabled ? DarkText : DarkMutedText,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    private static void PaintDisabledButton(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button ||
            button.Enabled ||
            Current != AppTheme.Dark ||
            (OriginalColorsByControl.TryGetValue(button, out var original) &&
             HasCustomBackColor(original.BackColor)))
        {
            return;
        }

        // WinForms' native disabled-button renderer ignores ForeColor and uses a system gray intended
        // for a light background. On a dark surface that becomes nearly black. Redraw only the disabled
        // state with the same layered palette used by the ToolStrip buttons.
        using var background = new SolidBrush(Color.FromArgb(48, 49, 53));
        using var border = new Pen(DarkBorder);
        e.Graphics.FillRectangle(background, button.ClientRectangle);
        e.Graphics.DrawRectangle(
            border,
            0,
            0,
            Math.Max(0, button.ClientSize.Width - 1),
            Math.Max(0, button.ClientSize.Height - 1));
        TextRenderer.DrawText(
            e.Graphics,
            button.Text,
            button.Font,
            button.ClientRectangle,
            DarkMutedText,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    private static void ApplyTabs(TabControl tabs, AppTheme theme)
    {
        if (tabs is ThemedTabControl themedTabs)
        {
            themedTabs.SetDarkMode(theme == AppTheme.Dark);
            return;
        }

        tabs.DrawItem -= DrawTab;
        if (theme == AppTheme.Dark)
        {
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem += DrawTab;
        }
        else
        {
            tabs.DrawMode = TabDrawMode.Normal;
        }
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
        {
            return;
        }

        var selected = e.Index == tabs.SelectedIndex;
        var bounds = e.Bounds;
        using var background = new SolidBrush(selected ? DarkInput : DarkSurface);
        using var textBrush = new SolidBrush(DarkText);
        e.Graphics.FillRectangle(background, bounds);
        TextRenderer.DrawText(
            e.Graphics,
            tabs.TabPages[e.Index].Text,
            tabs.Font,
            bounds,
            textBrush.Color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void ApplyToolStrip(ToolStrip strip, AppTheme theme)
    {
        var dark = theme == AppTheme.Dark;
        strip.BackColor = dark ? DarkSurface : SystemColors.Control;
        strip.ForeColor = dark ? DarkText : SystemColors.ControlText;
        strip.Renderer = dark
            ? new ToolStripProfessionalRenderer(new DarkColorTable())
            : new ToolStripProfessionalRenderer();

        foreach (ToolStripItem item in strip.Items)
        {
            ApplyToolStripItem(item, theme);
        }
    }

    private static void ApplyToolStripItem(ToolStripItem item, AppTheme theme)
    {
        item.BackColor = theme == AppTheme.Dark ? DarkSurface : SystemColors.Control;
        item.ForeColor = theme == AppTheme.Dark ? DarkText : SystemColors.ControlText;
        if (item is not ToolStripDropDownItem dropDown)
        {
            return;
        }

        dropDown.DropDown.BackColor = item.BackColor;
        dropDown.DropDown.ForeColor = item.ForeColor;
        dropDown.DropDown.Renderer = theme == AppTheme.Dark
            ? new ToolStripProfessionalRenderer(new DarkColorTable())
            : new ToolStripProfessionalRenderer();
        foreach (ToolStripItem child in dropDown.DropDownItems)
        {
            ApplyToolStripItem(child, theme);
        }
    }

    private static void ApplyTitleBar(Form form, AppTheme theme)
    {
        if (!form.IsHandleCreated || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var enabled = theme == AppTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
        }
        catch
        {
            // Older Windows versions may not support immersive dark title bars.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static bool HasCustomBackColor(Color color)
        => color != Color.Empty &&
           color != SystemColors.Control &&
           color != SystemColors.Window &&
           color != Color.White;

    private static bool HasCustomForeColor(Color color)
        => color != Color.Empty &&
           color != SystemColors.ControlText &&
           color != SystemColors.WindowText &&
           color != Color.Black;

    private sealed record OriginalColors(Color BackColor, Color ForeColor);

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => DarkSurface;
        public override Color ToolStripGradientMiddle => DarkSurface;
        public override Color ToolStripGradientEnd => DarkSurface;
        public override Color MenuStripGradientBegin => DarkSurface;
        public override Color MenuStripGradientEnd => DarkSurface;
        public override Color StatusStripGradientBegin => DarkSurface;
        public override Color StatusStripGradientEnd => DarkSurface;
        public override Color ToolStripDropDownBackground => DarkSurface;
        public override Color ImageMarginGradientBegin => DarkSurface;
        public override Color ImageMarginGradientMiddle => DarkSurface;
        public override Color ImageMarginGradientEnd => DarkSurface;
        public override Color MenuItemSelected => Color.FromArgb(58, 60, 65);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(58, 60, 65);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(58, 60, 65);
        public override Color MenuItemPressedGradientBegin => DarkInput;
        public override Color MenuItemPressedGradientMiddle => DarkInput;
        public override Color MenuItemPressedGradientEnd => DarkInput;
        public override Color ButtonSelectedGradientBegin => Color.FromArgb(58, 60, 65);
        public override Color ButtonSelectedGradientEnd => Color.FromArgb(58, 60, 65);
        public override Color ButtonPressedGradientBegin => Color.FromArgb(67, 69, 74);
        public override Color ButtonPressedGradientEnd => Color.FromArgb(67, 69, 74);
        public override Color ButtonCheckedGradientBegin => Color.FromArgb(45, 90, 120);
        public override Color ButtonCheckedGradientEnd => Color.FromArgb(45, 90, 120);
        public override Color MenuItemBorder => Accent;
        public override Color ButtonSelectedBorder => Accent;
        public override Color SeparatorDark => DarkBorder;
        public override Color SeparatorLight => DarkSurface;
        public override Color ToolStripBorder => DarkBorder;
    }
}

internal sealed class ThemedTabControl : TabControl
{
    private bool _darkMode;

    public void SetDarkMode(bool enabled)
    {
        _darkMode = enabled;
        SetStyle(ControlStyles.UserPaint, enabled);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, enabled);
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_darkMode)
        {
            base.OnPaintBackground(e);
            return;
        }

        e.Graphics.Clear(Color.FromArgb(32, 33, 36));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_darkMode)
        {
            base.OnPaint(e);
            return;
        }

        var surface = Color.FromArgb(42, 43, 46);
        var selectedSurface = Color.FromArgb(51, 52, 56);
        var border = Color.FromArgb(67, 69, 74);
        var text = Color.FromArgb(222, 224, 228);

        e.Graphics.Clear(Color.FromArgb(32, 33, 36));
        var pageBounds = DisplayRectangle;
        using (var pageBackground = new SolidBrush(Color.FromArgb(32, 33, 36)))
        using (var pageBorder = new Pen(border))
        {
            e.Graphics.FillRectangle(pageBackground, pageBounds);
            e.Graphics.DrawRectangle(
                pageBorder,
                pageBounds.X,
                pageBounds.Y,
                Math.Max(0, pageBounds.Width - 1),
                Math.Max(0, pageBounds.Height - 1));
        }

        for (var i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            var selected = i == SelectedIndex;
            using var background = new SolidBrush(selected ? selectedSurface : surface);
            using var tabBorder = new Pen(selected ? Color.FromArgb(92, 95, 102) : border);
            e.Graphics.FillRectangle(background, bounds);
            e.Graphics.DrawRectangle(
                tabBorder,
                bounds.X,
                bounds.Y,
                Math.Max(0, bounds.Width - 1),
                Math.Max(0, bounds.Height - 1));
            TextRenderer.DrawText(
                e.Graphics,
                TabPages[i].Text,
                Font,
                bounds,
                text,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }
    }
}
