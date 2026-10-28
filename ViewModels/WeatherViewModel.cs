using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WindowsNotch.ViewModels;

public partial class WeatherViewModel : ObservableObject
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _weatherPath;
    private readonly DispatcherTimer _refreshTimer;

    private double _latitude = 4.1755;
    private double _longitude = 73.5093;
    private bool _hasCustomLocation = false;
    private string _liveWeatherEffect = "Sun";

    [ObservableProperty]
    private string _cityName = "Locating...";

    [ObservableProperty]
    private string _citySearchQuery = string.Empty;

    [ObservableProperty]
    private double _temperatureC = 28;

    [ObservableProperty]
    private double _highC = 30;

    [ObservableProperty]
    private double _lowC = 26;

    [ObservableProperty]
    private int _humidity = 75;

    [ObservableProperty]
    private double _windKmh = 12;

    [ObservableProperty]
    private string _conditionText = "Clear Sky";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveVisualEffect))]
    [NotifyPropertyChangedFor(nameof(IsSunEffect))]
    [NotifyPropertyChangedFor(nameof(IsCloudEffect))]
    [NotifyPropertyChangedFor(nameof(IsRainEffect))]
    [NotifyPropertyChangedFor(nameof(IsAutoMode))]
    [NotifyPropertyChangedFor(nameof(WeatherEmoji))]
    [NotifyPropertyChangedFor(nameof(EffectStatusText))]
    private string _effectMode = "Auto"; // "Auto", "Sun", "Cloud", "Rain"

    public string ActiveVisualEffect => EffectMode == "Auto" ? _liveWeatherEffect : EffectMode;

    public bool IsSunEffect => string.Equals(ActiveVisualEffect, "Sun", StringComparison.OrdinalIgnoreCase);
    public bool IsCloudEffect => string.Equals(ActiveVisualEffect, "Cloud", StringComparison.OrdinalIgnoreCase);
    public bool IsRainEffect => string.Equals(ActiveVisualEffect, "Rain", StringComparison.OrdinalIgnoreCase);
    public bool IsAutoMode => string.Equals(EffectMode, "Auto", StringComparison.OrdinalIgnoreCase);

    public string TemperatureText => $"{Math.Round(TemperatureC):0}°C";
    public string ShortBadgeText => $"{WeatherEmoji} {Math.Round(TemperatureC):0}°";
    public string DetailsText => $"H:{Math.Round(HighC):0}° L:{Math.Round(LowC):0}° · 💧{Humidity}% · 💨{Math.Round(WindKmh):0}km/h";

    public string WeatherEmoji => ActiveVisualEffect switch
    {
        "Rain" => "🌧️",
        "Cloud" => "☁️",
        _ => "☀️"
    };

    public string EffectStatusText => EffectMode == "Auto"
        ? $"Eye Animation: Auto ({_liveWeatherEffect})"
        : $"Eye Animation: {EffectMode} (Pinned)";

    public event Action? WeatherEffectChanged;

    public WeatherViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dupinDir = Path.Combine(appData, "Dupin");
        Directory.CreateDirectory(dupinDir);
        _weatherPath = Path.Combine(dupinDir, "weather.json");

        LoadState();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(15)
        };
        _refreshTimer.Tick += async (s, e) => await RefreshWeatherAsync();
        _refreshTimer.Start();

        _ = RefreshWeatherAsync();
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_weatherPath))
            {
                string json = File.ReadAllText(_weatherPath);
                var dto = JsonSerializer.Deserialize<WeatherStateDto>(json);
                if (dto != null)
                {
                    _latitude = dto.Latitude;
                    _longitude = dto.Longitude;
                    _hasCustomLocation = dto.HasCustomLocation;
                    if (!string.IsNullOrWhiteSpace(dto.CityName))
                        CityName = dto.CityName;
                    TemperatureC = dto.TemperatureC;
                    HighC = dto.HighC;
                    LowC = dto.LowC;
                    Humidity = dto.Humidity;
                    WindKmh = dto.WindKmh;
                    if (!string.IsNullOrWhiteSpace(dto.ConditionText))
                        ConditionText = dto.ConditionText;
                    if (!string.IsNullOrWhiteSpace(dto.LiveWeatherEffect))
                        _liveWeatherEffect = dto.LiveWeatherEffect;
                    if (!string.IsNullOrWhiteSpace(dto.EffectMode))
                        EffectMode = dto.EffectMode;
                    NotifyComputedProperties();
                }
            }
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            var dto = new WeatherStateDto
            {
                Latitude = _latitude,
                Longitude = _longitude,
                HasCustomLocation = _hasCustomLocation,
                CityName = CityName,
                TemperatureC = TemperatureC,
                HighC = HighC,
                LowC = LowC,
                Humidity = Humidity,
                WindKmh = WindKmh,
                ConditionText = ConditionText,
                LiveWeatherEffect = _liveWeatherEffect,
                EffectMode = EffectMode
            };
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_weatherPath, json);
        }
        catch { }
    }

    [RelayCommand]
    public void SetEffectMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        EffectMode = mode;
        NotifyComputedProperties();
        SaveState();
        WeatherEffectChanged?.Invoke();
    }

    [RelayCommand]
    public async Task SearchCityAsync()
    {
        string q = CitySearchQuery?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(q)) return;

        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=1&language=en&format=json";
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                _latitude = first.GetProperty("latitude").GetDouble();
                _longitude = first.GetProperty("longitude").GetDouble();
                CityName = first.GetProperty("name").GetString() ?? q;
                _hasCustomLocation = true;
                CitySearchQuery = string.Empty;
                await FetchOpenMeteoAsync();
                SaveState();
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task RefreshWeatherAsync()
    {
        try
        {
            if (!_hasCustomLocation)
            {
                await TryDetectLocationAsync();
            }
            await FetchOpenMeteoAsync();
            SaveState();
        }
        catch { }
    }

    private async Task TryDetectLocationAsync()
    {
        try
        {
            string json = await _http.GetStringAsync("https://ipwho.is/");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("latitude", out var latProp) &&
                doc.RootElement.TryGetProperty("longitude", out var lonProp))
            {
                _latitude = latProp.GetDouble();
                _longitude = lonProp.GetDouble();
                if (doc.RootElement.TryGetProperty("city", out var cityProp) && !string.IsNullOrWhiteSpace(cityProp.GetString()))
                {
                    CityName = cityProp.GetString()!;
                }
                return;
            }
        }
        catch { }

        try
        {
            string json = await _http.GetStringAsync("https://get.geojs.io/v1/ip/geo.json");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("latitude", out var latProp) &&
                doc.RootElement.TryGetProperty("longitude", out var lonProp) &&
                double.TryParse(latProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(lonProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                _latitude = lat;
                _longitude = lon;
                if (doc.RootElement.TryGetProperty("city", out var cityProp) && !string.IsNullOrWhiteSpace(cityProp.GetString()))
                {
                    CityName = cityProp.GetString()!;
                }
            }
        }
        catch { }
    }

    private async Task FetchOpenMeteoAsync()
    {
        string latStr = _latitude.ToString(CultureInfo.InvariantCulture);
        string lonStr = _longitude.ToString(CultureInfo.InvariantCulture);
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=auto";

        string json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("current", out var current))
        {
            if (current.TryGetProperty("temperature_2m", out var t))
                TemperatureC = t.GetDouble();
            if (current.TryGetProperty("relative_humidity_2m", out var h))
                Humidity = h.GetInt32();
            if (current.TryGetProperty("wind_speed_10m", out var w))
                WindKmh = w.GetDouble();
            if (current.TryGetProperty("weather_code", out var wc))
            {
                int code = wc.GetInt32();
                (ConditionText, _liveWeatherEffect) = MapWmoCode(code);
            }
        }

        if (doc.RootElement.TryGetProperty("daily", out var daily))
        {
            if (daily.TryGetProperty("temperature_2m_max", out var maxArr) && maxArr.GetArrayLength() > 0)
                HighC = maxArr[0].GetDouble();
            if (daily.TryGetProperty("temperature_2m_min", out var minArr) && minArr.GetArrayLength() > 0)
                LowC = minArr[0].GetDouble();
        }

        if (CityName == "Locating...")
            CityName = "Local Weather";

        NotifyComputedProperties();
        WeatherEffectChanged?.Invoke();
    }

    private static (string Condition, string Effect) MapWmoCode(int code)
    {
        return code switch
        {
            0 => ("Clear Sky", "Sun"),
            1 => ("Mostly Clear", "Sun"),
            2 => ("Partly Cloudy", "Cloud"),
            3 => ("Overcast", "Cloud"),
            45 or 48 => ("Foggy", "Cloud"),
            51 or 53 or 55 => ("Light Drizzle", "Rain"),
            56 or 57 => ("Freezing Drizzle", "Rain"),
            61 or 63 or 65 => ("Rain Showers", "Rain"),
            66 or 67 => ("Freezing Rain", "Rain"),
            71 or 73 or 75 or 77 => ("Snowfall", "Rain"),
            80 or 81 or 82 => ("Rain Showers", "Rain"),
            85 or 86 => ("Snow Showers", "Rain"),
            95 or 96 or 99 => ("Thunderstorm", "Rain"),
            _ => ("Partly Cloudy", "Cloud")
        };
    }

    private void NotifyComputedProperties()
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(ShortBadgeText));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(ActiveVisualEffect));
        OnPropertyChanged(nameof(IsSunEffect));
        OnPropertyChanged(nameof(IsCloudEffect));
        OnPropertyChanged(nameof(IsRainEffect));
        OnPropertyChanged(nameof(IsAutoMode));
        OnPropertyChanged(nameof(WeatherEmoji));
        OnPropertyChanged(nameof(EffectStatusText));
    }

    private sealed class WeatherStateDto
    {
        public double Latitude { get; set; } = 4.1755;
        public double Longitude { get; set; } = 73.5093;
        public bool HasCustomLocation { get; set; }
        public string CityName { get; set; } = "Malé";
        public double TemperatureC { get; set; } = 29;
        public double HighC { get; set; } = 31;
        public double LowC { get; set; } = 26;
        public int Humidity { get; set; } = 78;
        public double WindKmh { get; set; } = 14;
        public string ConditionText { get; set; } = "Clear Sky";
        public string LiveWeatherEffect { get; set; } = "Sun";
        public string EffectMode { get; set; } = "Auto";
    }
}

