using System.Collections.Generic;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchy;

/// <summary>Copiados (its own page): on/off, disk or memory, limits, ignored apps, clear.</summary>
public partial class SettingsWindow
{
    private StackPanel ClipPage()
    {
        var p = Page("clip");
        ClipRows(Section(p), Section(p, Loc.T("s.sec.keep")), Section(p, Loc.T("s.sec.privacy")));
        return p;
    }

    private void ClipRows(StackPanel sec, StackPanel keep, StackPanel privacy)
    {
        Row(sec, Loc.T("s.cl.on"), Loc.T("s.cl.keep.d"), Toggle(S.ClipEnabled, v => S.ClipEnabled = v));
        ShortcutRow(sec);
        Row(keep, Loc.T("s.cl.disk"), Loc.T("s.cl.disk.d"), Toggle(S.ClipSaveToDisk, v => S.ClipSaveToDisk = v));
        Row(keep, Loc.T("s.cl.max"), Loc.T("s.cl.max.d"),
            Segmented(new[] { (50, "50"), (200, "200"), (500, "500") }, S.ClipMaxItems, v => S.ClipMaxItems = v));
        Row(keep, Loc.T("s.cl.days"), Loc.T("s.cl.days.d"),
            Segmented(new[] { (1, "1"), (7, "7"), (30, "30"), (90, "90") }, S.ClipMaxDays, v => S.ClipMaxDays = v));

        var box = new TextBox
        {
            Text = string.Join(", ", S.ClipIgnoredApps), Width = 230, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Center
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            var list = box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.SequenceEqual(S.ClipIgnoredApps)) return;
            S.ClipIgnoredApps = list;
            AppSettings.Commit();
        };
        Row(privacy, Loc.T("s.cl.ignore"), Loc.T("s.cl.ignore.d"), box);

        Row(privacy, Loc.T("s.cl.clear"), Loc.T("s.cl.clear.d"), ActionButton(Loc.T("s.cl.clear.b"), () =>
        {
            if (Island?.ClipHistory is not { } clip) return;
            var gone = clip.Unpinned;
            clip.Remove(gone);
            ClipboardService.Discard(gone);
        }, danger: true));
    }

    // ---------------------------------------------------------------- the shortcut: a field that records it

    /// <summary>What a shortcut field records and how it's told to the app.</summary>
    private sealed record ShortcutSpec(string Label, Func<string> Get, Action<string> Set, Action<string> Apply, Func<bool> Taken, Action Pause,
        Func<string, string[], string> Describe, string OffText, string NeedModText, bool KeyAlone);

    private void ShortcutRow(StackPanel sec) => ShortcutRow(sec, new ShortcutSpec(Loc.T("s.cl.key"),
        () => S.ClipHotkey, v => S.ClipHotkey = v, v => Island?.ClipHistory.SetHotkey(v, force: true), () => Island?.ClipHistory.HotkeyTaken == true,
        () => Island?.ClipHistory.PauseHotkey(),
        (name, labels) => KeyboardHook.Normalize(name) == "Win+V" ? Loc.T("s.cl.key.winv") : Loc.F("s.cl.key.set", string.Join(" + ", labels)),
        Loc.T("s.cl.key.off"), Loc.T("s.cl.key.needmod"), false));

    /// <summary>The keys of a saved shortcut ("Snapshot" is the Print Screen key alone).</summary>
    private static string[] ShortcutLabels(string name) => KeyboardHook.Labels(name);

    private void ShortcutRow(StackPanel sec, ShortcutSpec spec, bool stacked = false)
    {
        KeyboardHook? recHook = null; // (each field records on its own: two pages' fields must not share one)
        var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(dot);
        inner.Children.Add(keys);
        var edge = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
        var field = new Border { MinWidth = 170, Height = 32, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 0, 9, 0), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderBrush = edge, BorderThickness = new Thickness(1.5), Child = inner };
        Button record = null!, remove = null!;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(field);
        TextBlock desc = null!;
        Color accent = _accent, bad = Color.FromRgb(0xFF, 0x8A, 0x7A);

        void Text(string text, Color? color = null)
        {
            desc.Text = text;
            desc.Foreground = color is Color c ? new SolidColorBrush(c) : Dim(0x80);
        }
        void Show(IEnumerable<string> labels, bool waiting)
        {
            keys.Children.Clear();
            var list = labels.ToList();
            foreach (var l in list)
            {
                var cap = KeyCap(l);
                if (!waiting) cap.Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
                keys.Children.Add(cap);
            }
            if (waiting && list.Count > 0) keys.Children.Add(new TextBlock { Text = "+", Foreground = Dim(0x77), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            if (list.Count == 0) keys.Children.Add(new TextBlock { Text = Loc.T(waiting ? "s.cl.key.press" : "s.cl.key.empty"), FontSize = 11.5, Foreground = Dim(0x66), VerticalAlignment = VerticalAlignment.Center });
        }
        void Idle()
        {
            dot.Visibility = Visibility.Collapsed;
            field.BorderBrush = edge;
            field.Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            var labels = ShortcutLabels(spec.Get());
            Show(labels, false);
            if (record.Content is TextBlock t) t.Text = Loc.T("s.cl.key.rec");
            remove.IsEnabled = labels.Length > 0;
            bool taken = spec.Taken();
            if (taken) Text(Loc.T("s.cl.key.taken"), bad);
            else if (labels.Length == 0) Text(spec.OffText);
            else Text(spec.Describe(spec.Get(), labels));
        }
        void Stop()
        {
            recHook?.Dispose();
            recHook = null;
        }
        void Finish(string? name)
        {
            Stop();
            string before = spec.Get();
            if (name != null)
            {
                spec.Set(name);
                AppSettings.Commit();
            }
            spec.Apply(spec.Get());
            if (name != null && spec.Taken())
            {
                // someone else has it: the one before stays
                spec.Set(before);
                AppSettings.Commit();
                spec.Apply(before);
                Idle();
                Text(Loc.F("s.cl.key.busy", string.Join(" + ", ShortcutLabels(name))), bad);
                field.BorderBrush = new SolidColorBrush(bad);
                return;
            }
            if (name != null) RaiseCheer();
            Idle();
        }
        void Record()
        {
            if (recHook != null)
            {
                Finish(null); // (Cancelar)
                return;
            }
            spec.Pause(); // (pressing the old one now must not open the island)
            uint held = 0;
            dot.Visibility = Visibility.Visible;
            field.BorderBrush = new SolidColorBrush(accent);
            field.Background = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));
            if (record.Content is TextBlock t) t.Text = Loc.T("s.cl.key.cancel");
            Show(Array.Empty<string>(), true);
            Text(Loc.T("s.cl.key.recording"));
            IEnumerable<string> HeldLabels() => KeyboardHook.Labels(KeyboardHook.Format(held, 0x41)).SkipLast(1);
            recHook = new KeyboardHook((vk, down) =>
            {
                uint mod = KeyboardHook.ModOf(vk);
                Dispatcher.BeginInvoke(() =>
                {
                    if (recHook == null) return;
                    if (mod != 0)
                    {
                        held = down ? held | mod : held & ~mod;
                        Show(HeldLabels(), true);
                        return;
                    }
                    if (!down) return;
                    if (vk == 0x1B && held == 0)
                    {
                        Finish(null); // Esc
                        return;
                    }
                    if (held == 0 && spec.KeyAlone && KeyboardHook.IsBareKey(vk))
                    {
                        Finish(KeyboardHook.FormatBare(vk));
                        return;
                    }
                    if (held == 0)
                    {
                        Text(spec.NeedModText, bad);
                        return;
                    }
                    Finish(KeyboardHook.Format(held, vk));
                });
                return true; // (nothing reaches Windows or the apps while recording)
            });
            if (!recHook.Active) Finish(null);
        }

        record = ActionButton(Loc.T("s.cl.key.rec"), Record, primary: true);
        remove = ActionButton(Loc.T("s.cl.key.remove"), () =>
        {
            Stop();
            spec.Set("");
            AppSettings.Commit();
            spec.Apply("");
            Idle();
        });
        record.Margin = new Thickness(8, 0, 0, 0);
        controls.Children.Add(record);
        controls.Children.Add(remove);
        desc = Row(sec, spec.Label, " ", controls, below: stacked);
        Idle();
        Deactivated += (_, _) => { if (recHook != null) Finish(null); };
        Closed += (_, _) => Stop();
    }
}
