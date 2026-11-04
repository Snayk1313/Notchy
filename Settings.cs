using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Notchy;

public enum IslandSize { Compact, Normal, Large }
public enum IslandStyle { Attached, Floating }
public enum OpenMode { Hover, Click }
public enum ClockMode { Auto, H12, H24 }
public enum AnimSpeed { Fast, Normal, Smooth }

public sealed class AppSettings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notchy");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Raised (on the UI thread) after any change made through the settings window.</summary>
    public static event Action? Changed;

    public static AppSettings Current { get; private set; } = Load();

    // General
    public string Language { get; set; } = "pt";
    public AnimSpeed Speed { get; set; } = AnimSpeed.Normal;
    public bool GameMode { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;

    // Island
    public IslandSize Size { get; set; } = IslandSize.Normal;
    public IslandStyle Style { get; set; } = IslandStyle.Attached;
    public int SeeThrough { get; set; } = 0;        // 0..60 %
    public bool MusicColor { get; set; } = true;     // tint the expanded island with the album colour
    public OpenMode Open { get; set; } = OpenMode.Hover;
    public ClockMode Clock { get; set; } = ClockMode.Auto;
    public bool LyricsEnabled { get; set; } = true;
    public bool VolumeOsd { get; set; } = true;
    public int HoverDelayMs { get; set; } = 180;
    public bool BlurEnabled { get; set; } = false;
    public int BlurIntensity { get; set; } = 60;     // 0..100
    public bool IslandShadow { get; set; } = true;
    public int SettingsUiSize { get; set; } = 1;     // settings window: 0 small, 1 medium, 2 large
    public int GlowLevel { get; set; } = 1;          // 0 off, 1 subtle, 2 medium, 3 strong
    public int GradientStrength { get; set; } = 35; // coloured gradient behind the expanded tabs, 0..100
    public int BlurTint { get; set; } = 30;          // 0..100 darkening over the glass
    public bool EyesUseAlbumColor { get; set; } = true;
    public string EyeColor { get; set; } = "#FFFFFF";
    public bool LivelyEyes { get; set; } = true;
    public bool ScrollVolume { get; set; } = true;
    public bool ShelfEnabled { get; set; } = true;
    public bool BatteryAlerts { get; set; } = true;
    public int BatteryLowPercent { get; set; } = 20;
    public bool ClaudePod { get; set; } = true;

    // Sounds
    public bool SoundsEnabled { get; set; } = true;
    public int SoundVolume { get; set; } = 45;       // 0..100
    public bool SoundOpenClose { get; set; } = true;
    public bool SoundClicks { get; set; } = true;
    public bool SoundFocus { get; set; } = true;

    // Claude Code
    public bool ClaudeEnabled { get; set; } = true;
    public bool ClaudeSoundDone { get; set; } = true;
    public bool ClaudeSoundAttention { get; set; } = true;
    public bool ClaudeWindowsNotifications { get; set; } = false;
    public bool ClaudeShowInCompact { get; set; } = true;
    public string ChatTranslateTo { get; set; } = "pt";  // Notchy Chat: pt / en / es

    // Spotify Web API (your own app's Client ID; the refresh token is DPAPI-encrypted)
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyToken { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Save and notify everyone listening.</summary>
    public static void Commit()
    {
        Current.Save();
        Changed?.Invoke();
    }

    public static void ResetAll()
    {
        Current = new AppSettings();
        Commit();
    }

    public double SpeedFactor => Speed switch { AnimSpeed.Fast => 0.7, AnimSpeed.Smooth => 1.3, _ => 1.0 };
    public double SizeScale => Size switch { IslandSize.Compact => 0.88, IslandSize.Large => 1.12, _ => 1.0 };
}

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Notchy";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, false);
    }
}
