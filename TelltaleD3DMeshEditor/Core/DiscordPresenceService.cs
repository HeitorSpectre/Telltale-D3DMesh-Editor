using DiscordRPC;

namespace TelltaleD3DMeshEditor.Core;

public sealed class DiscordPresenceService : IDisposable
{
    // Create one Discord Developer Portal application for the tool, paste its public client/application
    // ID here, then upload Rich Presence assets with the keys used below. Users will not configure this.
    private const string ApplicationId = "1520212634904035328";
    private const string ToolImageKey = "logo_640_320";
    private const string ToolName = "Telltale D3DMesh Editor";

    private DiscordRpcClient? _client;
    private bool _enabled;
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private GameConfig _game = GameConfig.Generic;
    private string? _fileName;
    private bool _combinedModel;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (!enabled)
        {
            DisposeClient();
            return;
        }

        EnsureClient();
        UpdatePresence();
    }

    public void SetActivity(GameConfig game, string? fileName, bool combinedModel = false)
    {
        _game = game;
        _fileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        _combinedModel = combinedModel;
        UpdatePresence();
    }

    public void ResetTimer()
    {
        _startedAtUtc = DateTime.UtcNow;
        UpdatePresence();
    }

    public void Dispose()
    {
        DisposeClient();
    }

    private void EnsureClient()
    {
        if (_client is not null || string.IsNullOrWhiteSpace(ApplicationId))
        {
            return;
        }

        try
        {
            _client = new DiscordRpcClient(ApplicationId);
            _client.Initialize();
        }
        catch
        {
            DisposeClient();
        }
    }

    private void UpdatePresence()
    {
        if (!_enabled)
        {
            return;
        }

        EnsureClient();
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = _fileName is null
                    ? "Browsing files"
                    : _combinedModel
                        ? $"Editing combined model: {_fileName}"
                        : $"Editing: {_fileName}",
                State = _game.Id == GameId.Generic
                    ? "Game: Auto / Generic"
                    : $"Game: {_game.DisplayName}",
                Timestamps = new Timestamps(_startedAtUtc),
                Assets = new Assets
                {
                    LargeImageKey = ToolImageKey,
                    LargeImageText = ToolName,
                    SmallImageKey = GetGameImageKey(_game.Id),
                    SmallImageText = _game.DisplayName,
                },
            });
        }
        catch
        {
            DisposeClient();
        }
    }

    private static string? GetGameImageKey(GameId id)
        => id switch
        {
            GameId.WolfAmongUs => "twau",
            GameId.WalkingDead => "twd",
            GameId.WalkingDeadSeason2 => "twds2",
            GameId.WalkingDeadMichonne => "twdm",
            GameId.MinecraftStoryModeGroup => "mcsm",
            GameId.MinecraftStoryMode => "mcsms1",
            GameId.MinecraftStoryModeSeason2 => "mcsms2",
            GameId.TalesFromTheBorderlands => "tftb",
            GameId.TalesFromTheBorderlands2014 => "tftb2014",
            GameId.TalesFromTheBorderlandsE3 => "tftbe3",
            GameId.TalesFromTheBorderlandsOld => "tftbold",
            GameId.TalesFromTheBorderlands2021 => "tftb2021",
            GameId.GameOfThrones => "got",
            GameId.BackToTheFuture => "bttf",
            GameId.BackToTheFutureEpisode1 => "bttf101",
            GameId.BackToTheFutureEpisode2 => "bttf102",
            GameId.BackToTheFutureEpisode3 => "bttf103",
            GameId.BackToTheFutureEpisode4 => "bttf104",
            GameId.BackToTheFutureEpisode5 => "bttf105",
            _ => null,
        };

    private void DisposeClient()
    {
        try
        {
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch
        {
            // Discord is optional; closing the editor must never fail because IPC is unavailable.
        }
        finally
        {
            _client = null;
        }
    }
}
