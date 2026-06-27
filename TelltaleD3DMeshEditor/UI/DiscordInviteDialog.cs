using System.Reflection;
using TelltaleD3DMeshEditor.Core.Localization;

namespace TelltaleD3DMeshEditor.UI;

public sealed class DiscordInviteDialog : Form
{
    private static readonly Color CommunityAccentColor = Color.FromArgb(106, 45, 54);
    private static readonly Color DiscordButtonColor = Color.FromArgb(88, 101, 242);

    public DiscordInviteDialog(Font ownerFont)
    {
        Text = "Telltale Modding Group";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = ownerFont;
        ClientSize = new Size(560, 390);
        BackColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var bannerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CommunityAccentColor,
            Margin = new Padding(0),
        };

        if (TryLoadBanner() is { } banner)
        {
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = banner,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = CommunityAccentColor,
                Margin = new Padding(0),
            };
            bannerPanel.Controls.Add(picture);
        }
        else
        {
            bannerPanel.Paint += (_, e) => PaintFallbackBanner(e.Graphics, bannerPanel.ClientRectangle);
        }

        var title = new Label
        {
            Text = "Telltale Modding Group",
            AutoSize = true,
            Font = new Font(ownerFont.FontFamily, 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(35, 39, 42),
            Margin = new Padding(24, 20, 24, 8),
        };

        var message = new Label
        {
            Text = Loc.T("dialog.discord.message"),
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Color.FromArgb(54, 57, 63),
            Margin = new Padding(24, 0, 24, 18),
        };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 0, 24, 22),
            Margin = new Padding(0),
        };

        var maybeLater = new Button
        {
            Text = Loc.T("dialog.discord.maybe_later"),
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(112, 34),
            Margin = new Padding(8, 0, 0, 0),
        };

        var joinDiscord = new Button
        {
            Text = Loc.T("dialog.discord.join"),
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(112, 34),
            BackColor = DiscordButtonColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(8, 0, 0, 0),
        };
        joinDiscord.FlatAppearance.BorderSize = 0;

        buttons.Controls.Add(joinDiscord);
        buttons.Controls.Add(maybeLater);

        root.Controls.Add(bannerPanel, 0, 0);
        root.Controls.Add(title, 0, 1);
        root.Controls.Add(message, 0, 2);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);
        AcceptButton = joinDiscord;
        CancelButton = maybeLater;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeBannerImages(Controls);
        }

        base.Dispose(disposing);
    }

    private static Image? TryLoadBanner()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TelltaleD3DMeshEditor.Resources.Images.TelltaleModdingGroup.png");
        if (stream is null)
        {
            return null;
        }

        using var loaded = Image.FromStream(stream);
        return new Bitmap(loaded);
    }

    private static void PaintFallbackBanner(Graphics graphics, Rectangle bounds)
    {
        using var brush = new SolidBrush(CommunityAccentColor);
        graphics.FillRectangle(brush, bounds);

        using var font = new Font("Segoe UI", 28f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString("Discord", font, textBrush, bounds, format);
    }

    private static void DisposeBannerImages(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (control is PictureBox { Image: { } image })
            {
                image.Dispose();
            }

            DisposeBannerImages(control.Controls);
        }
    }
}
