using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Notchy;

/// <summary>A notification from another app (WhatsApp, Discord, Telegram…).</summary>
/// <param name="Avatar">The sender's picture when the app sends one (WhatsApp does), else the app's icon.</param>
/// <param name="IsPhoto">True when <paramref name="Avatar"/> is the sender's photo (drawn round), false for the app icon.</param>
public sealed record Notice(uint Id, string App, string AppId, string Title, string Body, DateTime Time, BitmapSource? Avatar, bool IsPhoto)
{
    /// <summary>
    /// The notification's content and tag as they were when it arrived, kept in memory only (never written to disk):
    /// the app removes the notification from Windows once you open it, and a second click still needs them.
    /// </summary>
    public string? Payload { get; init; }
    public string? Tag { get; init; }
    /// <summary>Set on the calendar's own notices: the type of the event, for its icon in the history.</summary>
    public CalType? CalendarType { get; init; }
}

/// <summary>
/// Reads Windows' notifications (the same ones in the notification center). Windows only tells Store apps when one
/// arrives, so this checks every 1.5 s. Texts come from Windows' notification API; the sender's photo isn't exposed
/// there, so it's read from the notification's own content in Windows' notification database (local, read-only):
/// apps put it as the "app logo override" image (WhatsApp: a link to the profile picture; others: a local file).
/// Nothing leaves the PC except downloading that picture from the app's own server.
/// </summary>
public sealed class NotificationService
{
    private static readonly HttpClient Http = FastHttp.Client(TimeSpan.FromSeconds(8));
    private static readonly string DbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");

    private readonly HashSet<uint> _seen = new();
    private readonly Dictionary<string, BitmapSource?> _appIcons = new();
    private System.Windows.Threading.DispatcherTimer? _timer;
    private bool _primed, _busy;

    public bool Available { get; private set; }

    /// <summary>Windows has answered whether Notchy may read notifications (<see cref="Available"/> means something from here on).</summary>
    public bool AccessKnown { get; private set; }
    public event Action? AccessChecked;

    /// <summary>The latest notifications, newest first (kept while Notchy runs).</summary>
    public List<Notice> History { get; } = new();

    public event Action<Notice>? Arrived;
    public event Action? HistoryChanged;

    /// <summary>Puts one of Notchy's own notices (the calendar's) in the history.</summary>
    public void AddLocal(Notice notice)
    {
        History.Insert(0, notice);
        if (History.Count > 60) History.RemoveAt(History.Count - 1);
        HistoryChanged?.Invoke();
    }

    public async void Start()
    {
        try
        {
            var l = UserNotificationListener.Current;
            Available = await l.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Available = false;
        }
        AccessKnown = true;
        AccessChecked?.Invoke();
        if (!Available) return;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var list = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = new List<UserNotification>();
            foreach (var n in list)
                if (_seen.Add(n.Id) && _primed) fresh.Add(n);
            // the first look only learns what's already there: no pop-ups for old notifications
            if (!_primed)
            {
                _primed = true;
                foreach (var n in list.OrderBy(n => n.CreationTime).TakeLast(15))
                    if (await ToNoticeAsync(n, avatar: true) is { } old) History.Insert(0, old); // (with the photos, like when they arrived)
                HistoryChanged?.Invoke();
                return;
            }
            foreach (var n in fresh.OrderBy(n => n.CreationTime))
            {
                var notice = await ToNoticeAsync(n, avatar: true);
                if (notice == null) continue;
                History.Insert(0, notice);
                if (History.Count > 60) History.RemoveAt(History.Count - 1);
                HistoryChanged?.Invoke();
                Arrived?.Invoke(notice);
            }
            PollTrayApps();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Clear()
    {
        History.Clear();
        HistoryChanged?.Invoke();
    }

    // ---- tray apps during "Do not disturb" ----------------------------------------------

    /// <summary>The id Windows makes up for a program that notifies through a tray balloon (Lightshot…).</summary>
    public const string TrayAppPrefix = "NotifyIconGeneratedAumid_";
    private const int TrayNameWaitSeconds = 30;

    private readonly Dictionary<string, long> _trayLast = new();
    private bool _watchTray;
    private long _trayFrom;

    /// <summary>
    /// Windows throws tray balloons away during "Do not disturb", text included, and only notes when the app sent one.
    /// While that's on (Notchy's "only on Notchy"), that note is enough for a card with the app's name and icon.
    /// </summary>
    public bool WatchTray
    {
        set
        {
            if (value && !_watchTray) _trayFrom = DateTime.UtcNow.ToFileTimeUtc();
            _watchTray = value;
        }
    }

    private void PollTrayApps()
    {
        if (!_watchTray) return;
        using var settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
        if (settings == null) return;
        foreach (string appId in settings.GetSubKeyNames().Where(k => k.StartsWith(TrayAppPrefix, StringComparison.Ordinal)))
        {
            using var key = settings.OpenSubKey(appId);
            if (key?.GetValue("LastNotificationAddedTime") is not long sent || sent <= _trayLast.GetValueOrDefault(appId, _trayFrom)) continue;
            var when = DateTime.FromFileTimeUtc(sent).ToLocalTime();
            string app = TrayAppName(appId);
            if (app.Length == 0 && (DateTime.Now - when).TotalSeconds < TrayNameWaitSeconds) continue; // Windows may not have written the name yet
            _trayLast[appId] = sent;
            if (app.Length == 0 || IsIgnoredApp(app, appId)) continue;

            if (!_appIcons.TryGetValue(appId, out var icon) && (icon = DesktopAppIcon.Find(appId, app)) != null) _appIcons[appId] = icon;
#if DEBUG
            try { File.AppendAllText(Path.Combine(AppSettings.Folder, "notice-icons.log"), $"{DateTime.Now:HH:mm:ss} BANDEJA {app} enviado={when:HH:mm:ss} imagem={icon?.PixelWidth}x{icon?.PixelHeight}\n"); } catch { }
#endif
            var notice = new Notice(0, app, appId, Loc.T("notif.tray"), "", when, icon, false);
            History.Insert(0, notice);
            if (History.Count > 60) History.RemoveAt(History.Count - 1);
            HistoryChanged?.Invoke();
            Arrived?.Invoke(notice);
        }
    }

    private static string TrayAppName(string appId)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + appId);
        return key?.GetValue("DisplayName") as string ?? "";
    }

    /// <summary>Claude has its own tab and bubble; Notchy's own messages are already on the island.</summary>
    private static bool IsIgnoredApp(string app, string appId) =>
        app.Contains("Claude", StringComparison.OrdinalIgnoreCase) || app.Contains("Notchy", StringComparison.OrdinalIgnoreCase) ||
        appId.Contains("Notchy", StringComparison.OrdinalIgnoreCase);

#if DEBUG
    /// <summary>Test: a real Windows notification. It goes out as Snipping Tool's because Windows only shows notifications from apps it knows.</summary>
    public void SendTest()
    {
        var xml = Windows.UI.Notifications.ToastNotificationManager.GetTemplateContent(Windows.UI.Notifications.ToastTemplateType.ToastText02);
        var lines = xml.GetElementsByTagName("text");
        lines[0].AppendChild(xml.CreateTextNode("Teste do Notchy"));
        lines[1].AppendChild(xml.CreateTextNode("Notificação de teste"));
        Windows.UI.Notifications.ToastNotificationManager
            .CreateToastNotifier("Microsoft.ScreenSketch_8wekyb3d8bbwe!App")
            .Show(new Windows.UI.Notifications.ToastNotification(xml));
    }
#endif

    private async Task<Notice?> ToNoticeAsync(UserNotification n, bool avatar)
    {
        string app, appId;
        try
        {
            app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            appId = n.AppInfo?.AppUserModelId ?? "";
        }
        catch
        {
            return null; // Windows has no app for some of its notifications: skip it, not the ones after it
        }
        if (app.Length == 0 || IsIgnoredApp(app, appId)) return null;
        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
        // one line each: messages with line breaks (a price, a link…) would spill out of the card
        string title = OneLine(texts?.Count > 0 ? texts[0].Text : app);
        string body = OneLine(texts?.Count > 1 ? string.Join(" ", texts.Skip(1).Select(t => t.Text)) : "");

        BitmapSource? picture = null;
        bool photo = false;
        if (avatar)
        {
            picture = await SenderPhotoAsync(n.Id, appId, title);
            photo = picture != null;
        }
        picture ??= await AppIconAsync(n, appId, app);
#if DEBUG
        try { File.AppendAllText(Path.Combine(AppSettings.Folder, "notice-icons.log"), $"{DateTime.Now:HH:mm:ss} {app} foto={photo} imagem={picture?.PixelWidth}x{picture?.PixelHeight}\n"); } catch { }
#endif
        var notice = new Notice(n.Id, app, appId, title, body, n.CreationTime.LocalDateTime, picture, photo);
        return notice with { Payload = PayloadFor(notice), Tag = TagFor(notice) };
    }

    private static string OneLine(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    // ---- the sender's photo -----------------------------------------------------------

    /// <summary>The "app logo override" image of the notification (the sender's photo), from Windows' notification database.</summary>
    internal static async Task<BitmapSource?> SenderPhotoAsync(uint id, string appId, string title)
    {
        try
        {
            string? payload = ReadPayload(id, appId, title);
            if (payload == null) return null;
            var tag = Regex.Matches(payload, "<image[^>]*>").Select(m => m.Value).FirstOrDefault(t => t.Contains("appLogoOverride"));
            var src = tag == null ? null : Regex.Match(tag, "src=\"([^\"]+)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(src)) return null;
            src = System.Net.WebUtility.HtmlDecode(src);

            byte[]? bytes = null;
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) bytes = await Http.GetByteArrayAsync(src);
            else if (Uri.TryCreate(src, UriKind.Absolute, out var u) && u.IsFile && File.Exists(u.LocalPath)) bytes = await File.ReadAllBytesAsync(u.LocalPath);
            else if (src.StartsWith("ms-appdata:///local/", StringComparison.OrdinalIgnoreCase))
            {
                // a Store app's own folder: %LOCALAPPDATA%\Packages\<family>\LocalState\…
                string family = appId.Split('!')[0];
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, "LocalState",
                    Uri.UnescapeDataString(src["ms-appdata:///local/".Length..]).Replace('/', '\\'));
                if (File.Exists(path)) bytes = await File.ReadAllBytesAsync(path);
            }
            return bytes == null ? null : Decode(bytes);
        }
        catch
        {
            return null; // no photo: the app's icon will do
        }
    }

    /// <summary>The notification's tag (Electron apps such as Discord use it as the notification's id).</summary>
    public static string? TagFor(Notice n)
    {
        try
        {
            if (!File.Exists(DbPath)) return null;
            using var con = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Cache=Shared");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Tag FROM Notification WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", (long)n.Id);
            return cmd.ExecuteScalar() is { } v ? AsText(v) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The notification's content as the app sent it (to open it like Windows does: NotificationOpener).</summary>
    public static string? PayloadFor(Notice n)
    {
        try
        {
            return ReadPayload(n.Id, n.AppId, n.Title);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The notification's content as the app sent it (read-only; by id, else the newest one of that app with that title).</summary>
    private static string? ReadPayload(uint id, string appId, string title)
    {
        if (!File.Exists(DbPath)) return null;
        using var con = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Cache=Shared");
        con.Open();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT Payload FROM Notification WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", (long)id);
            if (cmd.ExecuteScalar() is { } p) return AsText(p);
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT n.Payload FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                                WHERE h.PrimaryId = $app AND n.Type = 'toast' ORDER BY n.ArrivalTime DESC LIMIT 5";
            cmd.Parameters.AddWithValue("$app", appId);
            using var r = cmd.ExecuteReader();
            string? first = null;
            while (r.Read())
            {
                string text = AsText(r.GetValue(0));
                first ??= text;
                if (title.Length > 0 && text.Contains(System.Net.WebUtility.HtmlEncode(title))) return text;
            }
            return first;
        }
    }

    private static string AsText(object v) => v is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : v.ToString() ?? "";

    // ---- the app's icon ----------------------------------------------------------------

    private async Task<BitmapSource?> AppIconAsync(UserNotification n, string appId, string app)
    {
        if (_appIcons.TryGetValue(appId, out var cached)) return cached;
        BitmapSource? icon = null;
        try
        {
            var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logo != null)
            {
                using var stream = await logo.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                icon = Decode(ms.ToArray());
            }
        }
        catch
        {
            // desktop apps have no logo here
        }
        icon ??= DesktopAppIcon.Find(appId, app);
        if (icon != null) _appIcons[appId] = icon; // (not found: the next notification tries again)
        return icon;
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 96;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the app a notification came from (Store and desktop apps alike, by its app id).</summary>
    public static void OpenApp(string appId)
    {
        if (appId.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
