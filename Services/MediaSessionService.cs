using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using WindowsNotch.Models;

namespace WindowsNotch.Services;

public class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly DispatcherTimer _timelinePollTimer;

    public event Action<MediaTrack>? MediaTrackChanged;
    public event Action<TimeSpan, TimeSpan>? TimelineUpdated;
    public event Action<bool>? PlaybackStateChanged;

    public MediaTrack CurrentTrack { get; private set; } = new MediaTrack();

    public MediaSessionService()
    {
        _timelinePollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timelinePollTimer.Tick += OnTimelinePollTick;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnSessionManagerCurrentSessionChanged;
                HookSession(_sessionManager.GetCurrentSession());
                await RefreshMediaPropertiesAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaSessionService] Initialization error: {ex.Message}");
        }
    }

    private void OnSessionManagerCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        HookSession(sender.GetCurrentSession());
        _ = RefreshMediaPropertiesAsync();
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            _timelinePollTimer.Start();
        }
        else
        {
            _timelinePollTimer.Stop();
            CurrentTrack = new MediaTrack();
            MediaTrackChanged?.Invoke(CurrentTrack);
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshMediaPropertiesAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        UpdatePlaybackInfo();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimeline();
    }

    private void OnTimelinePollTick(object? sender, EventArgs e)
    {
        if (_currentSession != null && CurrentTrack.IsPlaying)
        {
            UpdateTimeline();
        }
    }

    public async Task RefreshMediaPropertiesAsync()
    {
        if (_currentSession == null)
        {
            CurrentTrack = new MediaTrack();
            MediaTrackChanged?.Invoke(CurrentTrack);
            return;
        }

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (props != null)
            {
                BitmapImage? thumbnail = null;
                if (props.Thumbnail != null)
                {
                    try
                    {
                        using var streamRef = await props.Thumbnail.OpenReadAsync();
                        using var stream = streamRef.AsStreamForRead();
                        var memStream = new MemoryStream();
                        await stream.CopyToAsync(memStream);
                        memStream.Position = 0;

                        thumbnail = new BitmapImage();
                        thumbnail.BeginInit();
                        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                        thumbnail.StreamSource = memStream;
                        thumbnail.EndInit();
                        thumbnail.Freeze();
                    }
                    catch
                    {
                        thumbnail = null;
                    }
                }

                CurrentTrack.Title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown Track" : props.Title;
                CurrentTrack.Artist = string.IsNullOrWhiteSpace(props.Artist) ? "Unknown Artist" : props.Artist;
                CurrentTrack.AlbumTitle = props.AlbumTitle ?? "";
                CurrentTrack.Thumbnail = thumbnail;

                UpdatePlaybackInfo();
                UpdateTimeline();

                MediaTrackChanged?.Invoke(CurrentTrack);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaSessionService] Error refreshing media properties: {ex.Message}");
        }
    }

    private void UpdatePlaybackInfo()
    {
        if (_currentSession == null) return;

        try
        {
            var playbackInfo = _currentSession.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                CurrentTrack.IsPlaying = isPlaying;
                PlaybackStateChanged?.Invoke(isPlaying);
            }
        }
        catch { }
    }

    private void UpdateTimeline()
    {
        if (_currentSession == null) return;

        try
        {
            var timeline = _currentSession.GetTimelineProperties();
            if (timeline != null)
            {
                CurrentTrack.Position = timeline.Position;
                CurrentTrack.Duration = timeline.EndTime;
                TimelineUpdated?.Invoke(timeline.Position, timeline.EndTime);
            }
        }
        catch { }
    }

    public async Task<bool> PlayPauseToggleAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                return await _currentSession.TryTogglePlayPauseAsync();
            }
            catch { }
        }
        return false;
    }

    public async Task<bool> SkipNextAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                return await _currentSession.TrySkipNextAsync();
            }
            catch { }
        }
        return false;
    }

    public async Task<bool> SkipPreviousAsync()
    {
        if (_currentSession != null)
        {
            try
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
            catch { }
        }
        return false;
    }
}
