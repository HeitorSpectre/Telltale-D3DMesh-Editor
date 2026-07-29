using System.Numerics;
using System.Diagnostics;
using TelltaleD3DMeshEditor.Core.Localization;
using TelltaleD3DMeshEditor.Export;
using TelltaleD3DMeshEditor.Viewer;

namespace TelltaleD3DMeshEditor.UI;

// In-viewer animation player: pick a discovered .anm, scrub/play it, and the preview mesh
// deforms through the same pose pipeline used by Pose mode. Decoding happens lazily per
// selected animation; bones whose CRC64 is not in the current skeleton are simply ignored.
public sealed class AnimationPlayerPanel : Panel
{
    private sealed record Channel(List<float> Times, List<Vector3>? Translations, List<Quaternion>? Rotations);

    private readonly MeshPreviewControl _preview;
    private readonly ComboBox _animCombo = new();
    private readonly Button _playButton = new();
    private readonly TrackBar _timeline = new();
    private readonly Label _timeLabel = new();
    private readonly Button _closeButton = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Stopwatch _playbackClock = new();

    private List<AnimationCollector.Candidate> _candidates = [];
    private Dictionary<ulong, List<Channel>> _channelsByBone = new();
    private bool _additive;
    private float _duration;
    private float _time;
    private bool _playing;
    private bool _suppressScrub;
    private int _decodeGeneration;

    public event EventHandler? PanelClosed;

    public AnimationPlayerPanel(MeshPreviewControl preview)
    {
        _preview = preview;
        Dock = DockStyle.Bottom;
        Height = 58;
        Padding = new Padding(6, 4, 6, 2);
        Visible = false;

        _animCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _animCombo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _animCombo.SelectedIndexChanged += async (_, _) => await LoadSelectedAnimationAsync();

        _playButton.Text = Loc.T("animplayer.play");
        _playButton.AutoSize = true;
        _playButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _playButton.Click += (_, _) => SetPlaying(!_playing);

        _closeButton.Text = Loc.T("animplayer.close");
        _closeButton.AutoSize = true;
        _closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeButton.Click += (_, _) => ClosePanel();

        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;
        _timeline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _timeline.ValueChanged += (_, _) =>
        {
            if (_suppressScrub || _duration <= 0f)
            {
                return;
            }

            _time = _timeline.Value / 1000f * _duration;
            ApplyCurrentFrame();
        };

        _timeLabel.AutoSize = true;
        _timeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _timeLabel.Text = "0.00 / 0.00s";

        _timer.Tick += (_, _) =>
        {
            AdvanceFromPlaybackClock();
        };
        // WinForms timers share the UI message queue. A continuous camera drag can keep that queue
        // occupied with mouse/paint messages, delaying Tick and making the animation appear paused.
        // Consume the same stopwatch from drag events so camera and pose advance in one rendered frame.
        _preview.MouseMove += PreviewMouseMove;

        Controls.AddRange([_animCombo, _playButton, _closeButton, _timeline, _timeLabel]);
        Resize += (_, _) => LayoutControls();
        LayoutControls();
    }

    private void LayoutControls()
    {
        var right = ClientSize.Width - Padding.Right;
        _closeButton.Location = new Point(right - _closeButton.Width, Padding.Top);
        _playButton.Location = new Point(_closeButton.Left - _playButton.Width - 4, Padding.Top);
        _animCombo.Location = new Point(Padding.Left, Padding.Top + 1);
        _animCombo.Width = Math.Max(80, _playButton.Left - Padding.Left - 6);
        _timeLabel.Location = new Point(right - _timeLabel.Width, Padding.Top + 30);
        _timeline.Location = new Point(Padding.Left, Padding.Top + 26);
        _timeline.Width = Math.Max(60, _timeLabel.Left - Padding.Left - 6);
    }

    // Shows the panel for a fresh set of discovered animations.
    public void Open(List<AnimationCollector.Candidate> candidates)
    {
        _candidates = candidates;
        _suppressScrub = true;
        _animCombo.Items.Clear();
        foreach (var candidate in candidates)
        {
            _animCombo.Items.Add(candidate.Name);
        }
        _suppressScrub = false;

        Visible = true;
        if (_animCombo.Items.Count > 0)
        {
            _animCombo.SelectedIndex = 0; // triggers decode + first frame
        }
    }

    public void ClosePanel()
    {
        SetPlaying(false);
        Visible = false;
        _channelsByBone = new Dictionary<ulong, List<Channel>>();
        _preview.ResetPose();
        PanelClosed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadSelectedAnimationAsync()
    {
        var index = _animCombo.SelectedIndex;
        if (index < 0 || index >= _candidates.Count)
        {
            return;
        }

        SetPlaying(false);
        var generation = ++_decodeGeneration;
        var candidate = _candidates[index];
        var decoded = await Task.Run(() => AnimationCollector.Decode([candidate]));
        if (generation != _decodeGeneration || IsDisposed)
        {
            return; // a newer selection superseded this decode
        }

        _channelsByBone = BuildChannels(decoded.Count > 0 ? decoded[0].Tracks : []);
        // Telltale "_add" animations are additive layers: their values are deltas from the rest
        // pose, so the preview must compose them instead of replacing the pose.
        _additive = candidate.Name.EndsWith("_add", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Name.Contains("_add_", StringComparison.OrdinalIgnoreCase);
        _duration = 0f;
        foreach (var channels in _channelsByBone.Values)
        {
            foreach (var channel in channels)
            {
                if (channel.Times.Count > 0)
                {
                    _duration = MathF.Max(_duration, channel.Times[^1]);
                }
            }
        }

        _time = 0f;
        ApplyCurrentFrame();
        SetPlaying(_channelsByBone.Count > 0);
    }

    private static Dictionary<ulong, List<Channel>> BuildChannels(List<AnimationExporter.BoneTrack> tracks)
    {
        var result = new Dictionary<ulong, List<Channel>>();
        foreach (var track in tracks)
        {
            if (track.Times.Count == 0)
            {
                continue;
            }

            var translations = track.Translations.Count == track.Times.Count ? track.Translations : null;
            var rotations = track.Rotations.Count == track.Times.Count ? track.Rotations : null;
            if (translations is null && rotations is null)
            {
                continue;
            }

            if (!result.TryGetValue(track.BoneHash, out var list))
            {
                result[track.BoneHash] = list = [];
            }

            list.Add(new Channel(track.Times, translations, rotations));
        }

        return result;
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing && _channelsByBone.Count > 0;
        _playButton.Text = Loc.T(_playing ? "animplayer.pause" : "animplayer.play");
        if (_playing)
        {
            _playbackClock.Restart();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _playbackClock.Reset();
        }
    }

    private void Advance(float deltaSeconds)
    {
        if (_duration <= 0f)
        {
            return;
        }

        _time = (_time + deltaSeconds) % _duration;

        ApplyCurrentFrame();
    }

    private void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        if (_playing && e.Button != MouseButtons.None)
        {
            AdvanceFromPlaybackClock();
        }
    }

    private void AdvanceFromPlaybackClock()
    {
        if (!_playing)
        {
            return;
        }

        var elapsed = (float)_playbackClock.Elapsed.TotalSeconds;
        if (elapsed <= 0f)
        {
            return;
        }

        _playbackClock.Restart();
        Advance(elapsed);
    }

    private void ApplyCurrentFrame()
    {
        var pose = new Dictionary<ulong, (Vector3? Translation, Quaternion? Rotation)>(_channelsByBone.Count);
        foreach (var (boneHash, channels) in _channelsByBone)
        {
            Vector3? translation = null;
            Quaternion? rotation = null;
            foreach (var channel in channels)
            {
                if (channel.Translations is not null)
                {
                    translation = SampleTranslation(channel, _time);
                }

                if (channel.Rotations is not null)
                {
                    rotation = SampleRotation(channel, _time);
                }
            }

            if (translation is not null || rotation is not null)
            {
                pose[boneHash] = (translation, rotation);
            }
        }

        _preview.ApplyAnimationLocalPose(pose, _additive);

        _suppressScrub = true;
        _timeline.Value = _duration > 0f
            ? Math.Clamp((int)(_time / _duration * 1000f), 0, 1000)
            : 0;
        _suppressScrub = false;
        _timeLabel.Text = $"{_time:0.00} / {_duration:0.00}s";
        LayoutControls();
    }

    private static (int Index, float Blend) FindSegment(List<float> times, float time)
    {
        if (time <= times[0])
        {
            return (0, 0f);
        }

        if (time >= times[^1])
        {
            return (times.Count - 1, 0f);
        }

        var low = 0;
        var high = times.Count - 1;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (times[mid] <= time)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var span = times[high] - times[low];
        return (low, span > 1e-6f ? (time - times[low]) / span : 0f);
    }

    private static Vector3 SampleTranslation(Channel channel, float time)
    {
        var (index, blend) = FindSegment(channel.Times, time);
        var values = channel.Translations!;
        return blend <= 0f || index + 1 >= values.Count
            ? values[index]
            : Vector3.Lerp(values[index], values[index + 1], blend);
    }

    private static Quaternion SampleRotation(Channel channel, float time)
    {
        var (index, blend) = FindSegment(channel.Times, time);
        var values = channel.Rotations!;
        return blend <= 0f || index + 1 >= values.Count
            ? values[index]
            : Quaternion.Slerp(values[index], values[index + 1], blend);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview.MouseMove -= PreviewMouseMove;
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
