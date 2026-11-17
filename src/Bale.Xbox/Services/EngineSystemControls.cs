using Windows.Media;
using Windows.Media.Playback;

namespace Bale.Xbox.Services;

/// <summary>A media button from the system controls, normalized: play, pause, or a skip forward/back.</summary>
internal enum SystemControlCommand { Play, Pause, Forward, Back }

/// <summary>
/// The system media controls (media remote, the Guide's media tile) for a backend without MediaPlayer's command
/// manager: the engine. Enables play/pause/next/previous/fast-forward/rewind on the view's
/// SystemMediaTransportControls, forwards their presses (on any thread) as <see cref="Command"/>, and keeps the
/// playback status and title current. One instance per mount; Dispose marks the session closed. UI thread.
/// </summary>
internal sealed partial class EngineSystemControls : IDisposable
{
    private readonly SystemMediaTransportControls _controls;
    private bool _disposed;

    public EngineSystemControls(string title, string? subtitle)
    {
        _controls = SystemMediaTransportControls.GetForCurrentView();
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.IsFastForwardEnabled = true;
        _controls.IsRewindEnabled = true;
        _controls.IsStopEnabled = false;
        _controls.PlaybackStatus = MediaPlaybackStatus.Changing;
        try
        {
            var display = _controls.DisplayUpdater;
            display.ClearAll();
            display.Type = MediaPlaybackType.Video;
            display.VideoProperties.Title = title;
            if (!string.IsNullOrWhiteSpace(subtitle)) display.VideoProperties.Subtitle = subtitle;
            display.Update();
        }
        catch (Exception ex) { Log.Info($"player: system controls display {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
        _controls.ButtonPressed += OnButtonPressed;
    }

    /// <summary>Raised on the system's thread; handlers marshal to the UI thread.</summary>
    public event Action<SystemControlCommand>? Command;

    /// <summary>Mirrors the backend's state onto the controls (and the Guide tile).</summary>
    public void Update(MediaPlaybackState state)
    {
        if (_disposed) return;
        try
        {
            _controls.PlaybackStatus = state switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                MediaPlaybackState.None => MediaPlaybackStatus.Closed,
                _ => MediaPlaybackStatus.Changing,
            };
        }
        catch (Exception ex) { Log.Info($"player: system controls status {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        SystemControlCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemControlCommand.Play,
            SystemMediaTransportControlsButton.Pause => SystemControlCommand.Pause,
            SystemMediaTransportControlsButton.Next or SystemMediaTransportControlsButton.FastForward => SystemControlCommand.Forward,
            SystemMediaTransportControlsButton.Previous or SystemMediaTransportControlsButton.Rewind => SystemControlCommand.Back,
            _ => null,
        };
        if (command is { } value) Command?.Invoke(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
        }
        catch (Exception ex) { Log.Info($"player: system controls close {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
    }
}
