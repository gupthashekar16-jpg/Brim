using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Brim.Core;

namespace Brim.Services;

public sealed record WeatherHour(DateTime Time, double Temp, int Code, int Rain, bool Day);
public sealed record WeatherDay(DateTime Date, double Max, double Min, int Code, int Rain);

/// <summary>Current conditions and forecast from Open-Meteo (no key needed).</summary>
public sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static WeatherService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Brim/1.0");

    readonly AppSettings _s;
    public string City { get; private set; } = "";
    public bool Ready { get; private set; }
    public double Temp { get; private set; }
    public double Feels { get; private set; }
    public double High { get; private set; }
    public double Low { get; private set; }
    public int Code { get; private set; }
    public int Humidity { get; private set; }
    public double Wind { get; private set; }
    public int RainChance { get; private set; }
    public bool IsDay { get; private set; } = true;
    public DateTime Sunset { get; private set; }
    public DateTime Sunrise { get; private set; }
    public List<WeatherHour> Hours { get; private set; } = [];
    public List<WeatherDay> Days { get; private set; } = [];
    public DateTime UpdatedAt { get; private set; }
    public string Unit => _s.Units == "F" ? "°F" : "°C";
    public string WindUnit => _s.Units == "F" ? "mph" : "km/h";
    public event Action? Changed;

    double? _ipLat, _ipLon;
    string _ipCity = "";

    public WeatherService(AppSettings s)
    {
        _s = s;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        t.Tick += (_, _) => _ = RefreshAsync();
        t.Start();
    }

    public async Task RefreshAsync()
    {
        try
        {
            double lat, lon;
            if (_s.Lat is double la && _s.Lon is double lo) { lat = la; lon = lo; City = _s.City; }
            else
            {
                if (_ipLat == null) await LocateAsync();
                if (_ipLat == null) return;
                lat = _ipLat.Value; lon = _ipLon!.Value; City = _ipCity;
            }
            var inv = CultureInfo.InvariantCulture;
            var units = _s.Units == "F" ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,is_day" +
                      "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max" +
                      "&timezone=auto&forecast_days=7" + units;
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var r = doc.RootElement;
            var cur = r.GetProperty("current");
            Temp = cur.GetProperty("temperature_2m").GetDouble();
            Feels = cur.GetProperty("apparent_temperature").GetDouble();
            Humidity = (int)cur.GetProperty("relative_humidity_2m").GetDouble();
            Wind = cur.GetProperty("wind_speed_10m").GetDouble();
            Code = cur.GetProperty("weather_code").GetInt32();
            IsDay = cur.GetProperty("is_day").GetInt32() == 1;
            var curTime = DateTime.Parse(cur.GetProperty("time").GetString()!, inv);

            var h = r.GetProperty("hourly");
            var ht = h.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, inv)).ToList();
            var htemp = h.GetProperty("temperature_2m").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var hcode = h.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
            var hrain = h.GetProperty("precipitation_probability").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList();
            var hday = h.GetProperty("is_day").EnumerateArray().Select(x => x.GetInt32() == 1).ToList();
            var start = ht.FindIndex(t => t > curTime.AddHours(-1));
            Hours = [];
            for (int i = Math.Max(0, start); i < ht.Count && Hours.Count < 24; i++)
                Hours.Add(new WeatherHour(ht[i], htemp[i], hcode[i], hrain[i], hday[i]));
            RainChance = Hours.Count > 0 ? Hours[0].Rain : 0;

            var d = r.GetProperty("daily");
            var dt = d.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, inv)).ToList();
            var dmax = d.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var dmin = d.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var dcode = d.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
            var drain = d.GetProperty("precipitation_probability_max").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList();
            var dset = d.GetProperty("sunset").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, inv)).ToList();
            var drise = d.GetProperty("sunrise").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, inv)).ToList();
            Days = [];
            for (int i = 0; i < dt.Count; i++) Days.Add(new WeatherDay(dt[i], dmax[i], dmin[i], dcode[i], drain[i]));
            if (Days.Count > 0) { High = Days[0].Max; Low = Days[0].Min; Sunset = dset[0]; Sunrise = drise[0]; }
            Ready = true;
            UpdatedAt = DateTime.Now;
            Changed?.Invoke();
        }
        catch { }
    }

    async Task LocateAsync()
    {
        foreach (var url in new[] { "https://ipapi.co/json/", "https://ipwho.is/" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var r = doc.RootElement;
                if (r.TryGetProperty("latitude", out var la) && r.TryGetProperty("longitude", out var lo) && la.ValueKind == JsonValueKind.Number)
                {
                    _ipLat = la.GetDouble();
                    _ipLon = lo.GetDouble();
                    _ipCity = r.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                    return;
                }
            }
            catch { }
        }
    }

    /// <summary>Looks a city up and stores it as the fixed location. Returns false if not found.</summary>
    public async Task<bool> SetCityAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _s.Lat = _s.Lon = null; _s.City = ""; _s.Changed("City");
            await RefreshAsync();
            return true;
        }
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(name.Trim());
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return false;
            var f = res[0];
            _s.Lat = f.GetProperty("latitude").GetDouble();
            _s.Lon = f.GetProperty("longitude").GetDouble();
            _s.City = f.GetProperty("name").GetString() ?? name;
            _s.Changed("City");
            await RefreshAsync();
            return true;
        }
        catch { return false; }
    }
}
