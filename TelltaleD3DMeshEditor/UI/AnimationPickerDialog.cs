using TelltaleD3DMeshEditor.Core.Localization;
using TelltaleD3DMeshEditor.Export;

namespace TelltaleD3DMeshEditor.UI;

// Lists the .anm animations discovered for a model so the user can pick which ones to embed
// in the exported GLB. Discovery is name-based (fast); decoding happens after OK, on export.
public sealed class AnimationPickerDialog : Form
{
    private readonly List<AnimationCollector.Candidate> _allCandidates;
    private readonly HashSet<string> _checkedNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBox _search = new();
    private readonly CheckedListBox _list = new();
    private readonly Label _countLabel = new();

    public AnimationPickerDialog(string modelName, List<AnimationCollector.Candidate> candidates)
    {
        _allCandidates = candidates;

        Text = Loc.T("anim.dialog_title", modelName);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 360);
        Size = new Size(560, 560);

        var searchLabel = new Label
        {
            Text = Loc.T("anim.dialog_search"),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        _search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _search.TextChanged += (_, _) => RefreshList();

        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.CheckOnClick = true;
        _list.IntegralHeight = false;
        _list.HorizontalScrollbar = true;
        _list.ItemCheck += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            var name = _list.Items[e.Index]?.ToString() ?? string.Empty;
            if (e.NewValue == CheckState.Checked) _checkedNames.Add(name);
            else _checkedNames.Remove(name);
            BeginInvoke(UpdateCount);
        };

        var selectAll = new Button { Text = Loc.T("anim.dialog_select_all"), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        selectAll.Click += (_, _) => SetAllVisible(true);
        var selectNone = new Button { Text = Loc.T("anim.dialog_select_none"), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        selectNone.Click += (_, _) => SetAllVisible(false);

        _countLabel.AutoSize = true;
        _countLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        var okButton = new Button
        {
            Text = Loc.T("anim.dialog_export"),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK,
        };
        var cancelButton = new Button
        {
            Text = Loc.T("anim.dialog_cancel"),
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        const int margin = 10;
        searchLabel.Location = new Point(margin, margin + 4);
        _search.Location = new Point(searchLabel.Right + 6, margin);
        _search.Width = ClientSize.Width - _search.Left - margin;
        _list.Location = new Point(margin, _search.Bottom + 8);
        _list.Size = new Size(ClientSize.Width - margin * 2, ClientSize.Height - _list.Top - 78);
        selectAll.Location = new Point(margin, ClientSize.Height - 66);
        selectNone.Location = new Point(selectAll.Right + 6, ClientSize.Height - 66);
        _countLabel.Location = new Point(margin, ClientSize.Height - 30);
        okButton.Location = new Point(ClientSize.Width - margin - 170, ClientSize.Height - 38);
        cancelButton.Location = new Point(ClientSize.Width - margin - 84, ClientSize.Height - 38);

        Controls.AddRange([searchLabel, _search, _list, selectAll, selectNone, _countLabel, okButton, cancelButton]);

        // Re-anchor button rows relative to the resized client area.
        Resize += (_, _) =>
        {
            selectAll.Top = ClientSize.Height - 66;
            selectNone.Top = ClientSize.Height - 66;
        };

        RefreshList();
    }

    public List<AnimationCollector.Candidate> SelectedCandidates =>
        _allCandidates.Where(c => _checkedNames.Contains(c.Name)).ToList();

    private void RefreshList()
    {
        var filter = _search.Text?.Trim() ?? string.Empty;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var candidate in _allCandidates)
        {
            if (filter.Length > 0 && !candidate.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _list.Items.Add(candidate.Name, _checkedNames.Contains(candidate.Name));
        }
        _list.EndUpdate();
        UpdateCount();
    }

    private void SetAllVisible(bool check)
    {
        _list.BeginUpdate();
        for (var i = 0; i < _list.Items.Count; i++)
        {
            _list.SetItemChecked(i, check);
        }
        _list.EndUpdate();
        UpdateCount();
    }

    private void UpdateCount()
    {
        _countLabel.Text = Loc.T("anim.dialog_count", _checkedNames.Count, _allCandidates.Count);
    }
}
