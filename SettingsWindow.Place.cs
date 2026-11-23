using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>Configurações → Posição: the usual place, dragging, and the rules that move the island on their own.</summary>
public sealed partial class SettingsWindow
{
    private StackPanel PlacePage()
    {
        var p = Page("place");
        var main = Section(p);
        Row(main, Loc.T("s.place.normal"), Loc.T("s.place.normal.d"),
            Segmented(new[] { (IslandPlace.Left, Loc.T("isl.place.left")), (IslandPlace.Top, Loc.T("isl.place.top")), (IslandPlace.Right, Loc.T("isl.place.right")) },
                S.Place, v => S.Place = v));
        Row(main, Loc.T("s.place.drag"), Loc.T("s.place.drag.d"), Toggle(S.PlaceDrag, v => S.PlaceDrag = v));

        var rules = Section(p, Loc.T("s.place.rules"));
        foreach (var r in S.PlaceRules) RuleRow(rules, r);
        var foot = new Grid { Margin = new Thickness(16, 11, 16, 11) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(new TextBlock
        {
            Text = Loc.T(S.PlaceRules.Count == 0 ? "s.place.empty" : "s.place.full"), FontSize = 11.5, Foreground = Dim(0x80), TextWrapping = TextWrapping.Wrap,
            LineHeight = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0)
        });
        Button? add = null;
        add = ActionButton(Loc.T("s.place.add"), () => PickApp(add!, (proc, name) =>
        {
            S.PlaceRules.Add(new PlaceRule { Process = proc, Name = name, When = PlaceWhen.Fullscreen, Place = S.Place == IslandPlace.Right ? IslandPlace.Left : IslandPlace.Right });
            RulesChanged();
        }), primary: true);
        Grid.SetColumn(add, 1);
        foot.Children.Add(add);
        AddToCard(rules, foot);
        return p;
    }

    private void RulesChanged()
    {
        AppSettings.Commit();
        ShowPage(_page, animate: false);
    }

    /// <summary>"Quando [Zen] estiver [em tela cheia], a ilha vai para a [direita]", with a little drawing of the screen.</summary>
    private void RuleRow(StackPanel card, PlaceRule r)
    {
        var g = new Grid { Margin = new Thickness(16, 10, 12, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(RuleDrawing(r));

        var words = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Opacity = r.On ? 1 : 0.55 };
        void Say(string text) => words.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 5, 3) });
        Say(Loc.T("rule.when"));
        Border? app = null;
        app = Chip(string.IsNullOrEmpty(r.Name) ? r.Process : r.Name, () => PickApp(app!, (proc, name) =>
        {
            r.Process = proc;
            r.Name = name;
            RulesChanged();
        }));
        words.Children.Add(app);
        Say(Loc.T("rule.is"));
        Border? when = null;
        when = Chip(Loc.T(r.When == PlaceWhen.Fullscreen ? "rule.full" : "rule.front"), () => Menu(when!,
            (Loc.T("rule.full"), () => { r.When = PlaceWhen.Fullscreen; RulesChanged(); }),
            (Loc.T("rule.front"), () => { r.When = PlaceWhen.Front; RulesChanged(); })));
        words.Children.Add(when);
        Say(Loc.T(r.Place == IslandPlace.Top ? "rule.goes.m" : "rule.goes.f"));
        string SideName(IslandPlace p) => Loc.T(p switch { IslandPlace.Left => "isl.place.left", IslandPlace.Right => "isl.place.right", _ => "isl.place.top" }).ToLower(Loc.Culture);
        Border? side = null;
        side = Chip(SideName(r.Place), () => Menu(side!,
            (SideName(IslandPlace.Left), () => { r.Place = IslandPlace.Left; RulesChanged(); }),
            (SideName(IslandPlace.Right), () => { r.Place = IslandPlace.Right; RulesChanged(); }),
            (SideName(IslandPlace.Top), () => { r.Place = IslandPlace.Top; RulesChanged(); })));
        words.Children.Add(side);
        Grid.SetColumn(words, 1);
        g.Children.Add(words);

        var on = Toggle(r.On, v => { r.On = v; RulesChanged(); });
        on.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(on, 2);
        g.Children.Add(on);

        var remove = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(10, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = Loc.T("rule.remove"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, Foreground = Dim(0x99), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        remove.MouseEnter += (_, _) => remove.Background = Dim(0x14);
        remove.MouseLeave += (_, _) => remove.Background = Brushes.Transparent;
        remove.MouseLeftButtonUp += (_, _) =>
        {
            SoundService.Play(Sfx.Click);
            S.PlaceRules.Remove(r);
            RulesChanged();
        };
        Grid.SetColumn(remove, 3);
        g.Children.Add(remove);
        AddToCard(card, g);
    }

    /// <summary>A word of the rule you can change: a click shows the choices.</summary>
    private Border Chip(string text, Action click)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 3, 5, 3), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x2A, _accent.R, _accent.G, _accent.B)),
            Child = new TextBlock { Text = text + "  ▾", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        chip.MouseEnter += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x48, _accent.R, _accent.G, _accent.B));
        chip.MouseLeave += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x2A, _accent.R, _accent.G, _accent.B));
        chip.MouseLeftButtonUp += (_, _) =>
        {
            SoundService.Play(Sfx.Click);
            click();
        };
        return chip;
    }

    private static void Menu(FrameworkElement under, params (string label, Action pick)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = under, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, pick) in items)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => pick();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>The apps open now (with a window), to pick one for a rule.</summary>
    private static void PickApp(FrameworkElement under, Action<string, string> pick)
    {
        var apps = OpenApps();
        if (apps.Count == 0)
        {
            Menu(under, (Loc.T("rule.noapps"), () => { }));
            return;
        }
        Menu(under, apps.Select(a => (a.name, (Action)(() => pick(a.process, a.name)))).ToArray());
    }

    private static List<(string process, string name)> OpenApps()
    {
        var list = new List<(string process, string name)>();
        int own = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == own || p.MainWindowHandle == IntPtr.Zero) continue;
                    string name = p.ProcessName;
                    try
                    {
                        var desc = p.MainModule?.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) name = desc!;
                    }
                    catch
                    {
                        // access denied for some processes: the process name will do
                    }
                    list.Add((p.ProcessName, name));
                }
                catch
                {
                    // gone in the meantime
                }
            }
        }
        return list.GroupBy(a => a.process, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
            .OrderBy(a => a.name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The screen, the app (filling it, or a window in front) and where the island goes.</summary>
    private static UIElement RuleDrawing(PlaceRule r)
    {
        const double W = 84, H = 48;
        var c = new Canvas { Width = W, Height = H, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        var screen = new Border
        {
            Width = W, Height = H, CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x15, 0x59), Color.FromRgb(0x9C, 0x3A, 0x8F), 70)
        };
        c.Children.Add(screen);
        var app = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1A, 0x26)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x26, 0x35)) };
        if (r.When == PlaceWhen.Fullscreen)
        {
            app.Width = W;
            app.Height = H - 5;
            app.BorderThickness = new Thickness(0, 5, 0, 0);
            app.CornerRadius = new CornerRadius(6, 6, 0, 0);
        }
        else
        {
            app.Width = 46;
            app.Height = 28;
            app.BorderThickness = new Thickness(0, 4, 0, 0);
            app.CornerRadius = new CornerRadius(3);
            Canvas.SetLeft(app, 19);
            Canvas.SetTop(app, 9);
        }
        c.Children.Add(app);
        var island = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0A, 0x12)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), BorderThickness = new Thickness(1) };
        switch (r.Place)
        {
            case IslandPlace.Top:
                island.Width = 24; island.Height = 6; island.CornerRadius = new CornerRadius(0, 0, 3, 3);
                Canvas.SetLeft(island, W / 2 - 12);
                break;
            case IslandPlace.Left:
                island.Width = 6; island.Height = 15; island.CornerRadius = new CornerRadius(0, 3, 3, 0);
                Canvas.SetTop(island, H / 2 - 7.5);
                break;
            default:
                island.Width = 6; island.Height = 15; island.CornerRadius = new CornerRadius(3, 0, 0, 3);
                Canvas.SetLeft(island, W - 6);
                Canvas.SetTop(island, H / 2 - 7.5);
                break;
        }
        c.Children.Add(island);
        return new Border { CornerRadius = new CornerRadius(6), Child = c, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 6, 6) };
    }
}
