using System;
using System.Windows.Media.Imaging;

namespace WindowsNotch.Models;

public class MediaTrack
{
    public string Title { get; set; } = "No media playing";
    public string Artist { get; set; } = "";
    public string AlbumTitle { get; set; } = "";
    public BitmapImage? Thumbnail { get; set; }
    public bool IsPlaying { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public TimeSpan Position { get; set; } = TimeSpan.Zero;

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title) && Title != "No media playing";
    public double ProgressPercentage => Duration.TotalSeconds > 0 ? (Position.TotalSeconds / Duration.TotalSeconds) * 100.0 : 0;
}
