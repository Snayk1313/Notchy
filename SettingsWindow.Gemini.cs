using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchy;

/// <summary>Who answers in Notchy Chat (Claude or Gemini) and the free Gemini key: in the settings and in the assistant.</summary>
public partial class SettingsWindow
{
    private void ChatEngineRows(StackPanel section)
    {
        Row(section, Loc.T("s.gm.engine"), Loc.T("s.gm.engine.d"),
            Segmented(new[] { (false, "Claude"), (true, "Gemini") }, S.ChatUseGemini, v =>
            {
                S.ChatUseGemini = v;
                AppSettings.Commit();
                Dispatcher.InvokeAsync(Rebuild);
            }));
        if (!S.ChatUseGemini) return;

        if (GeminiKey.Exists)
        {
            var saved = new StackPanel { Orientation = Orientation.Horizontal };
            saved.Children.Add(StatusDot(Color.FromRgb(0x34, 0xC7, 0x59), Loc.T("s.gm.connected")));
            saved.Children.Add(ActionButton(Loc.T("s.gm.remove"), () =>
            {
                GeminiKey.Delete();
                Rebuild();
            }));
            Row(section, Loc.T("s.gm.key"), Loc.T("s.gm.saved.d"), saved);
            return;
        }

        Row(section, Loc.T("s.gm.get"), Loc.T("s.gm.get.d"), GeminiButton(Loc.T("s.gm.open"), OpenKeyPage));

        var box = new PasswordBox
        {
            Width = _setup ? 130 : 170, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        var paste = new StackPanel { Orientation = Orientation.Horizontal };
        paste.Children.Add(box);
        var slot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        paste.Children.Add(slot);
        TextBlock description = null!;
        void ShowConnect() => slot.Content = GeminiButton(Loc.T("s.gm.connect"), async () =>
        {
            string key = box.Password.Trim();
            if (key.Length == 0) return;
            slot.Content = Spinner();
            var result = await GeminiRunner.CheckKeyAsync(key);
            if (result == GeminiKeyCheck.Ok)
            {
                GeminiKey.Save(key);
                RaiseCheer();
                Rebuild();
                return;
            }
            description.Text = Loc.T(result == GeminiKeyCheck.Invalid ? "s.gm.invalid" : "s.gm.noconn");
            description.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));
            ShowConnect();
        }, primary: true);
        ShowConnect();
        description = Row(section, Loc.T("s.gm.key"), Loc.T("s.gm.key.d"), paste);
    }

    private UIElement GeminiButton(string text, Action action, bool primary = false) =>
        _setup ? SetupButton(text, action, primary) : ActionButton(text, action, primary);

    private static void OpenKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GeminiRunner.KeyPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
