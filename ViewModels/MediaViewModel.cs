using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsNotch.Models;
using WindowsNotch.Services;

namespace WindowsNotch.ViewModels;

public partial class MediaViewModel : ObservableObject
{
    private readonly MediaSessionService _mediaService;
    private readonly LyricsService _lyricsService;

    [ObservableProperty]
    private string _trackTitle = "No media playing";

    [ObservableProperty]
    private string _artistName = "Ready for playback";

    [ObservableProperty]
    private string _albumTitle = "";

    [ObservableProperty]
    private BitmapImage? _albumArt;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _position = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _positionText = "00:00";

    [ObservableProperty]
    private string _durationText = "00:00";

    [ObservableProperty]
    private ObservableCollection<SyncedLyric> _lyrics = new();

    [ObservableProperty]
    private int _currentLyricIndex = -1;

    [ObservableProperty]
    private string _currentLyricText = "No synced lyrics";

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _isLyricsLoading;

    public MediaViewModel(MediaSessionService mediaService, LyricsService lyricsService)
    {
        _mediaService = mediaService;
        _lyricsService = lyricsService;

        _mediaService.MediaTrackChanged += OnMediaTrackChanged;
        _mediaService.PlaybackStateChanged += OnPlaybackStateChanged;
        _mediaService.TimelineUpdated += OnTimelineUpdated;
    }

    private void OnMediaTrackChanged(MediaTrack track)
    {
        App.Current.Dispatcher.Invoke(async () =>
        {
            TrackTitle = track.Title;
            ArtistName = track.Artist;
            AlbumTitle = track.AlbumTitle;
            AlbumArt = track.Thumbnail;
            IsPlaying = track.IsPlaying;

            if (track.HasTrack)
            {
                await LoadLyricsAsync(track.Title, track.Artist, (int)track.Duration.TotalSeconds);
            }
            else
            {
                Lyrics.Clear();
                HasLyrics = false;
                CurrentLyricText = "No synced lyrics";
                CurrentLyricIndex = -1;
            }
        });
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            IsPlaying = isPlaying;
        });
    }

    private void OnTimelineUpdated(TimeSpan position, TimeSpan duration)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Position = position;
            Duration = duration;
            PositionText = FormatTime(position);
            DurationText = FormatTime(duration);
            Progress = duration.TotalSeconds > 0 ? (position.TotalSeconds / duration.TotalSeconds) * 100.0 : 0;

            UpdateActiveLyric(position);
        });
    }

    private void UpdateActiveLyric(TimeSpan currentPos)
    {
        if (Lyrics.Count == 0) return;

        int activeIdx = -1;
        for (int i = 0; i < Lyrics.Count; i++)
        {
            if (Lyrics[i].Timestamp <= currentPos)
            {
                activeIdx = i;
            }
            else
            {
                break;
            }
        }

        if (activeIdx != CurrentLyricIndex)
        {
            CurrentLyricIndex = activeIdx;
            for (int i = 0; i < Lyrics.Count; i++)
            {
                Lyrics[i].IsActive = (i == activeIdx);
                Lyrics[i].IsPast = (i < activeIdx);
            }

            if (activeIdx >= 0 && activeIdx < Lyrics.Count)
            {
                CurrentLyricText = Lyrics[activeIdx].Text;
            }
        }
    }

    public async Task LoadLyricsAsync(string track, string artist, int durationSec)
    {
        IsLyricsLoading = true;
        try
        {
            var result = await _lyricsService.FetchLyricsAsync(track, artist, durationSec);
            Lyrics.Clear();
            foreach (var item in result)
            {
                Lyrics.Add(item);
            }
            HasLyrics = Lyrics.Count > 0;
            CurrentLyricIndex = -1;
            CurrentLyricText = HasLyrics ? "Synced lyrics loaded" : "No synced lyrics available";
        }
        catch
        {
            HasLyrics = false;
            CurrentLyricText = "Could not load lyrics";
        }
        finally
        {
            IsLyricsLoading = false;
        }
    }

    private static string FormatTime(TimeSpan t)
    {
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        await _mediaService.PlayPauseToggleAsync();
    }

    [RelayCommand]
    public async Task SkipNextAsync()
    {
        await _mediaService.SkipNextAsync();
    }

    [RelayCommand]
    public async Task SkipPreviousAsync()
    {
        await _mediaService.SkipPreviousAsync();
    }
}
