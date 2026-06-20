using TelltaleD3DMeshEditor.Core;

namespace TelltaleD3DMeshEditor.UI;

public sealed class GameSelectionDialog : Form
{
    private readonly ComboBox _gameCombo = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();

    public GameConfig SelectedGame => (GameConfig)_gameCombo.SelectedItem!;

    public GameSelectionDialog(GameConfig current)
    {
        Text = "Select Game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 136);
        Font = new Font("Segoe UI", 9F);

        var label = new Label
        {
            Text = "Choose the game profile for these assets:",
            AutoSize = true,
            Location = new Point(12, 14),
        };

        _gameCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _gameCombo.Location = new Point(12, 42);
        _gameCombo.Size = new Size(336, 24);
        foreach (var game in GameConfig.All.Where(game => game.Id != GameId.Generic && !game.IsGameMenuGroup && !IsBlockedBackToTheFutureEpisode(game.Id)))
        {
            _gameCombo.Items.Add(game);
        }

        var selected = GameConfig.All.FirstOrDefault(game =>
            game.Id == current.Id &&
            game.Id != GameId.Generic &&
            !game.IsGameMenuGroup &&
            !IsBlockedBackToTheFutureEpisode(game.Id));
        _gameCombo.SelectedItem = selected ?? _gameCombo.Items.Cast<GameConfig>().FirstOrDefault();
        _gameCombo.DisplayMember = nameof(GameConfig.DisplayName);

        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Location = new Point(186, 94);
        _okButton.Size = new Size(75, 26);

        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Location = new Point(273, 94);
        _cancelButton.Size = new Size(75, 26);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        Controls.AddRange([label, _gameCombo, _okButton, _cancelButton]);
    }

    private static bool IsBlockedBackToTheFutureEpisode(GameId id)
        => id is GameId.BackToTheFutureEpisode2 or
                 GameId.BackToTheFutureEpisode3 or
                 GameId.BackToTheFutureEpisode4 or
                 GameId.BackToTheFutureEpisode5;
}
