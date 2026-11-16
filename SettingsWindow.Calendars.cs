using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Notchy;

/// <summary>
/// Calendário e clima › Agendas conectadas: the calendars read through their iCal link (Google, Outlook, iCloud…),
/// and "Conectar agenda": pick the service, see where its link is, paste it.
/// </summary>
public partial class SettingsWindow
{
    private bool _extWizard;
    private string _extProvider = "google";
    private string _extTyped = "";

    private static readonly (string key, string letter, Color color, string? url)[] ExtProviders =
    {
        ("google", "G", Color.FromRgb(0x4F, 0x8F, 0xF7), "https://calendar.google.com/calendar/r/settings"),
        ("outlook", "O", Color.FromRgb(0x0A, 0x64, 0xD2), "https://outlook.live.com/calendar/0/options/calendar/SharedCalendars"),
        ("icloud", "☁", Color.FromRgb(0x9A, 0xA0, 0xA6), "https://www.icloud.com/calendar"),
        ("samsung", "S", Color.FromRgb(0x14, 0x28, 0xA0), null),
    };

    private void ExternalCalendarRows(StackPanel page)
    {
        var cal = Island?.Calendar;
        if (cal == null) return;
        ExternalCalendarRowsInto(Section(page, Loc.T("s.sec.extcal")), cal);
    }

    /// <summary>The connected calendars and "Conectar agenda" (the settings' section, or a step of the assistant).</summary>
    private void ExternalCalendarRowsInto(StackPanel sec, CalendarService cal)
    {
        foreach (var x in cal.Externals)
        {
            var one = x;
            string desc = x.Error.Length > 0 ? x.Error : Loc.F("ext.status", Loc.T("ext.p." + x.Provider), Ago(x.LastSync), x.Events.Count);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(new TextBlock { Text = Loc.T("ext.notify"), FontSize = 11.5, Foreground = Dim(0x99), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            controls.Children.Add(Toggle(x.Notify, v => { one.Notify = v; cal.ChangedExternal(one); }));
            controls.Children.Add(ActionButton(Loc.T("ext.remove"), () => { cal.RemoveExternal(one); Rebuild(); }, danger: true));
            var d = Row(sec, x.Name, desc, controls);
            if (x.Error.Length > 0) d.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A));
            // the calendar's colour before its name, like on the island
            if (d.Parent is StackPanel texts && texts.Children[0] is TextBlock title)
            {
                title.TextWrapping = TextWrapping.NoWrap; // (a long address is cut with "…", not broken in the middle)
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.Inlines.Clear();
                title.Inlines.Add(new Run("●  ") { Foreground = new SolidColorBrush(x.Tint) });
                title.Inlines.Add(new Run(x.Name));
            }
        }
        if (!_extWizard)
            Row(sec, Loc.T("ext.add"), Loc.T("ext.add.d"), GeminiButton(Loc.T("ext.add.b"), () => { _extWizard = true; Rebuild(); }, primary: cal.Externals.Count == 0));
        else
        {
            var g = new Grid { Margin = new Thickness(16, 12, 16, 14), Tag = new GlideKey("wizard:" + _extProvider) };
            g.Children.Add(ExtWizard(cal));
            AddToCard(sec, g);
        }
    }

    private static string Ago(DateTime? when)
    {
        if (when is not DateTime w) return Loc.T("ext.ago.never");
        var span = DateTime.Now - w;
        return span.TotalMinutes < 1 ? Loc.T("ext.ago.now") : span.TotalHours < 1 ? Loc.F("ext.ago.min", (int)span.TotalMinutes) : Loc.F("ext.ago.h", (int)span.TotalHours);
    }

    /// <summary>1 · which service, 2 · where its link is, 3 · paste it.</summary>
    private FrameworkElement ExtWizard(CalendarService cal)
    {
        var p = new StackPanel();
        TextBlock Step(string key) => new() { Text = Loc.T(key), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_accent), Margin = new Thickness(0, p.Children.Count == 0 ? 0 : 14, 0, 7) };

        p.Children.Add(Step("ext.step1"));
        var tiles = new UniformGrid { Rows = 1, Columns = ExtProviders.Length };
        foreach (var (key, letter, color, _) in ExtProviders)
        {
            bool on = key == _extProvider;
            var logo = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = letter, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 10) };
            stack.Children.Add(logo);
            stack.Children.Add(new TextBlock { Text = Loc.T("ext.p." + key), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            var tile = new Border
            {
                Child = stack, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Background = Dim((byte)(on ? 0x16 : 0x08)), BorderThickness = new Thickness(1.5), BorderBrush = on ? new SolidColorBrush(_accent) : Brushes.Transparent
            };
            tile.MouseEnter += (_, _) => { if (!on) tile.Background = Dim(0x10); };
            tile.MouseLeave += (_, _) => { if (!on) tile.Background = Dim(0x08); };
            string k = key;
            tile.MouseLeftButtonUp += (_, _) => { _extProvider = k; Rebuild(); };
            tiles.Children.Add(tile);
        }
        p.Children.Add(tiles);

        var provider = Array.Find(ExtProviders, x => x.key == _extProvider);
        p.Children.Add(Step("ext.step2"));
        p.Children.Add(new TextBlock { Text = Loc.T("ext.help." + _extProvider), FontSize = 12, Foreground = Dim(0xBB), TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
        if (provider.url is string url)
        {
            var open = ActionButton(Loc.T("ext.open." + _extProvider), () =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            });
            open.Margin = new Thickness(0, 8, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(open);
        }

        var cancel = ActionButton(Loc.T("cal.cancel"), () => { _extWizard = false; _extTyped = ""; Rebuild(); });
        if (_extProvider == "samsung")
        {
            // nothing to paste: Samsung's calendar goes through a Google or Microsoft account
            cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Margin = new Thickness(0, 12, 0, 0);
            p.Children.Add(cancel);
            return p;
        }

        p.Children.Add(Step("ext.step3"));
        var box = new TextBox
        {
            Text = _extTyped, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center
        };
        box.TextChanged += (_, _) => _extTyped = box.Text;
        var status = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        Button connect = null!;
        connect = ActionButton(Loc.T("ext.add.b"), async () =>
        {
            if (box.Text.Trim().Length == 0) return;
            connect.IsEnabled = box.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            status.Foreground = Dim(0x99);
            status.Text = Loc.T("ext.reading");
            var error = await cal.AddExternalAsync(box.Text, _extProvider);
            if (error == null)
            {
                _extWizard = false;
                _extTyped = "";
                Rebuild();
                return;
            }
            connect.IsEnabled = box.IsEnabled = true;
            status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A));
            status.Text = error;
        }, primary: true);
        var line = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(connect, Dock.Right);
        line.Children.Add(cancel);
        line.Children.Add(connect);
        line.Children.Add(box);
        p.Children.Add(line);
        p.Children.Add(status);
        return p;
    }

    private void OnExternalsChanged()
    {
        // a refresh came in (new times, an error): redrawn, unless a link is being typed
        if (_page == "calendar" && !_extWizard && !Searching) Dispatcher.BeginInvoke(Rebuild);
    }
}
