using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

// A small bell on the closed island while an alarm is set. When 10 minutes are left the alarm's time shows next to it
// for 15 seconds, and the idle island stretches to fit. Only on the idle island and with music playing.
public partial class MainWindow
{
    private const double AlarmHintGap = 8, AlarmBellSize = 12, AlarmTimeGap = 4, IdleHintEdge = 13;
    private static readonly TimeSpan AlarmSoon = TimeSpan.FromMinutes(10), AlarmSoonShown = TimeSpan.FromSeconds(15);
    private DateTime? _soonFor;
    private DateTime _soonUntil;
    private static readonly Geometry BellGeometry = Geometry.Parse(
        "M12,2.5 A1.6,1.6 0 0 0 10.4,4.1 V4.6 A6.5,6.5 0 0 0 5.5,11 V14.6 L3.8,17.2 C3.5,17.7 3.8,18.4 4.5,18.4 H19.5 C20.2,18.4 20.5,17.7 20.2,17.2 " +
        "L18.5,14.6 V11 A6.5,6.5 0 0 0 13.6,4.6 V4.1 A1.6,1.6 0 0 0 12,2.5 Z M9.6,20 A2.5,2.5 0 0 0 14.4,20 Z");

    private FrameworkElement? _idleBell, _musicBell;
    private TextBlock? _idleSoon, _musicSoon;
    private bool _alarmHas;
    private string? _alarmSoon;
#if DEBUG
    private (bool has, string? soon)? _alarmHintForced; // (snapshot test)
#endif

    private (FrameworkElement host, FrameworkElement bell, TextBlock time) MakeAlarmHint()
    {
        var glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.6, Color = TimerColor };
        var bell = new Viewbox
        {
            Width = AlarmBellSize, Height = AlarmBellSize, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Effect = glow,
            Child = new Canvas { Width = 24, Height = 24, Children = { new Path { Data = BellGeometry, Fill = new SolidColorBrush(TimerColor) } } }
        };
        var time = new TextBlock
        {
            FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(TimerColor), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(AlarmTimeGap, 0, 0, 0), Visibility = Visibility.Collapsed
        };
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        host.Children.Add(bell);
        host.Children.Add(time);
        return (host, bell, time);
    }

    private void InitAlarmHint()
    {
        var idle = MakeAlarmHint();
        ((FrameworkElement)idle.host).HorizontalAlignment = HorizontalAlignment.Right;
        ((FrameworkElement)idle.host).Margin = new Thickness(0, 0, IdleHintEdge, 0);
        IdlePanel.Children.Add(idle.host);
        (_idleBell, _idleSoon) = (idle.bell, idle.time);

        var music = MakeAlarmHint();
        ((FrameworkElement)music.host).Margin = new Thickness(AlarmHintGap, 0, 0, 0);
        CompactAlarm.Children.Add(music.host);
        (_musicBell, _musicSoon) = (music.bell, music.time);
    }

    /// <summary>The idle island: the usual width, or stretched so the time fits without touching the eyes.</summary>
    private double IdleWidth
    {
        get
        {
            if (_alarmSoon == null || _idleSoon == null) return 120;
            double hint = IdleHintEdge + AlarmBellSize + AlarmTimeGap + MeasureText(_idleSoon, _alarmSoon);
            return Math.Max(120, Math.Ceiling(2 * (21 + hint)));
        }
    }

    /// <summary>The room the bell (and the time) take beside the music's visualizer.</summary>
    private double CompactAlarmExtra
    {
        get
        {
            if (_compactMode != CompactMode.Music || !_alarmHas || _musicSoon == null) return 0;
            return AlarmHintGap + AlarmBellSize + (_alarmSoon == null ? 0 : AlarmTimeGap + MeasureText(_musicSoon, _alarmSoon));
        }
    }

    private void UpdateAlarmHint(bool force = false)
    {
        if (_idleBell == null || _musicBell == null || _idleSoon == null || _musicSoon == null) return;
        bool has;
        string? soon = null;
#if DEBUG
        if (_alarmHintForced is { } f) (has, soon) = f;
        else
#endif
        {
            var next = _clock.NextAlarm();
            has = next != null;
            if (next is { } n && n.at - DateTime.Now <= AlarmSoon)
            {
                // once per alarm, and the 15 seconds only start when the time can actually be seen
                bool visible = _state == IslandState.Idle || (_state == IslandState.Compact && _compactMode == CompactMode.Music);
                if (_soonFor != n.at && visible)
                {
                    _soonFor = n.at;
                    _soonUntil = DateTime.UtcNow + AlarmSoonShown;
                }
                if (_soonFor == n.at && DateTime.UtcNow < _soonUntil) soon = FormatClock(n.at);
            }
        }
        bool changed = has != _alarmHas || soon != _alarmSoon;
        if (!changed && !force) return;
        _alarmHas = has;
        _alarmSoon = soon;

        var fade = TimeSpan.FromMilliseconds(Dur(220));
        _idleBell.BeginAnimation(OpacityProperty, new DoubleAnimation(has ? 1 : 0, fade));
        _musicBell.BeginAnimation(OpacityProperty, new DoubleAnimation(has && _compactMode == CompactMode.Music ? 1 : 0, fade));
        foreach (var t in new[] { _idleSoon, _musicSoon })
        {
            t.Text = soon ?? "";
            t.Visibility = soon == null ? Visibility.Collapsed : Visibility.Visible;
        }
        CompactAlarm.Visibility = has && _compactMode == CompactMode.Music ? Visibility.Visible : Visibility.Collapsed;
        IdlePanel.Width = IdleWidth;
        if (!changed) return;
        if (_state == IslandState.Idle) AnimateIslandSize(TargetSize(IslandState.Idle), bigMove: false);
        if (_compactMode == CompactMode.Music) FitCompactText();
    }
}
