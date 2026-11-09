using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace Notchy;

public enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Storm }

public sealed record WeatherHour(DateTime Time, double Temp, Sky Sky, bool Day);
public sealed record WeatherDay(DateTime Date, double Max, double Min, Sky Sky);

public sealed record WeatherNow(string City, double Temp, double Feels, Sky Sky, bool Day, double TodayMax, double TodayMin,
    IReadOnlyList<WeatherHour> Hours, IReadOnlyList<WeatherDay> Days, DateTime Fetched);

public enum WeatherProblem { None, NoLocation, Offline }

/// <summary>A city found by name (for picking the weather's place by hand).</summary>
public sealed record CityResult(string Name, string Region, double Lat, double Lon);

/// <summary>
/// The weather where you are: your position from Windows' location service, the forecast from Open-Meteo (free,
/// no account or key) and the city's name from BigDataCloud's free reverse geocoding. Refreshes every 30 minutes.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private (double lat, double lon)? _where;
    private DateTime _whereAt;
    private string _city = "";

    public WeatherNow? Current { get; private set; }
    public WeatherProblem Problem { get; private set; }
    public event Action? Changed;

    static WeatherService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy/2.1");

    private string _usedPlace = "";

    /// <summary>Automatic (Windows' location) or the city picked by hand, as a key: a change means fetching again.</summary>
    private static string PlaceKey => AppSettings.Current.WeatherAuto || AppSettings.Current.WeatherCity.Length == 0
        ? "auto" : $"{AppSettings.Current.WeatherLat:0.###},{AppSettings.Current.WeatherLon:0.###}";

    /// <summary>The place changed in the settings: fetch for the new one.</summary>
    public bool PlaceChanged => PlaceKey != _usedPlace;

    public async Task RefreshAsync(bool force = false)
    {
        if (PlaceChanged) force = true;
        if (!force && Current != null && DateTime.Now - Current.Fetched < TimeSpan.FromMinutes(25)) return;
        try
        {
            var st = AppSettings.Current;
            _usedPlace = PlaceKey;
            if (_usedPlace != "auto")
            {
                // the city you picked
                _where = (st.WeatherLat, st.WeatherLon);
                _whereAt = DateTime.MinValue; // (back to automatic: ask Windows again)
                _city = st.WeatherCity;
            }
            else if (_where == null || DateTime.Now - _whereAt > TimeSpan.FromHours(2)) await LocateAsync();
            if (_where is not { } w)
            {
                Problem = WeatherProblem.NoLocation;
                Changed?.Invoke();
                return;
            }
            Current = await ForecastAsync(w.lat, w.lon);
            Problem = WeatherProblem.None;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (Current == null) Problem = WeatherProblem.Offline;
        }
        Changed?.Invoke();
    }

    /// <summary>Asks Windows where we are (needs Location on and "let desktop apps access your location").</summary>
    private async Task LocateAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return;
            var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await geo.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
            var p = pos.Coordinate.Point.Position;
            _where = (p.Latitude, p.Longitude);
            _whereAt = DateTime.Now;
            _city = await CityAsync(p.Latitude, p.Longitude);
        }
        catch (Exception ex)
        {
            App.Log(ex); // location off: keep the last place we knew, if any
        }
    }

    /// <summary>Cities matching a name (Open-Meteo's free geocoding), with their state and country to tell them apart.</summary>
    public static async Task<IReadOnlyList<CityResult>> SearchAsync(string name)
    {
        var list = new List<CityResult>();
        if (name.Trim().Length < 2) return list;
        try
        {
            string lang = Loc.Language == "en" ? "en" : "pt";
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=6&language={lang}&format=json";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
            foreach (var r in results.EnumerateArray())
            {
                string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                string region = string.Join(" · ", new[] { Get("admin1"), Get("country") }.Where(x => x.Length > 0));
                list.Add(new CityResult(Get("name"), region, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return list;
    }

    private static async Task<string> CityAsync(double lat, double lon)
    {
        try
        {
            string lang = Loc.Language == "en" ? "en" : "pt";
            string url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={F(lat)}&longitude={F(lon)}&localityLanguage={lang}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var r = doc.RootElement;
            foreach (var key in new[] { "city", "locality", "principalSubdivision" })
                if (r.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch
        {
            // the name is a nicety
        }
        return "";
    }

    private async Task<WeatherNow> ForecastAsync(double lat, double lon)
    {
        string url = "https://api.open-meteo.com/v1/forecast" +
                     $"?latitude={F(lat)}&longitude={F(lon)}" +
                     "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                     "&hourly=temperature_2m,weather_code,is_day&forecast_hours=12" +
                     "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=6&timezone=auto";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        var cur = root.GetProperty("current");
        var hourly = root.GetProperty("hourly");
        var daily = root.GetProperty("daily");

        var hours = new List<WeatherHour>();
        var ht = hourly.GetProperty("time");
        for (int i = 0; i < ht.GetArrayLength(); i++)
            hours.Add(new WeatherHour(DateTime.Parse(ht[i].GetString()!, CultureInfo.InvariantCulture),
                hourly.GetProperty("temperature_2m")[i].GetDouble(), SkyOf(hourly.GetProperty("weather_code")[i].GetInt32()),
                hourly.GetProperty("is_day")[i].GetInt32() == 1));

        var days = new List<WeatherDay>();
        var dt = daily.GetProperty("time");
        for (int i = 0; i < dt.GetArrayLength(); i++)
            days.Add(new WeatherDay(DateTime.Parse(dt[i].GetString()!, CultureInfo.InvariantCulture),
                daily.GetProperty("temperature_2m_max")[i].GetDouble(), daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                SkyOf(daily.GetProperty("weather_code")[i].GetInt32())));

        return new WeatherNow(_city, cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("apparent_temperature").GetDouble(),
            SkyOf(cur.GetProperty("weather_code").GetInt32()), cur.GetProperty("is_day").GetInt32() == 1,
            days.Count > 0 ? days[0].Max : 0, days.Count > 0 ? days[0].Min : 0, hours, days, DateTime.Now);
    }

    /// <summary>WMO weather codes (what Open-Meteo returns) → a few kinds of sky.</summary>
    private static Sky SkyOf(int code) => code switch
    {
        0 => Sky.Clear,
        1 or 2 => Sky.PartlyCloudy,
        3 => Sky.Cloudy,
        45 or 48 => Sky.Fog,
        >= 51 and <= 67 or >= 80 and <= 82 => Sky.Rain,
        >= 71 and <= 77 or 85 or 86 => Sky.Snow,
        >= 95 => Sky.Storm,
        _ => Sky.Cloudy
    };

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
