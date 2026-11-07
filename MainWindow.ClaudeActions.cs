using System;
using System.Windows;
using System.Windows.Threading;

namespace Notchy;

/// <summary>The Claude tab's buttons: go back to the session, and answer permission requests on the island.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _openHintTimer;

    private void ClaudeOpen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SoundService.Play(Sfx.Click);
        var c = _claude.Active;
        if (c == null) return;
        if (ClaudeHost.Open(c) == ClaudeHost.OpenResult.NotFound)
        {
            // say so on the button for a moment
            ClaudeOpenText.Text = Loc.T("claude.open.notfound");
            _openHintTimer?.Stop();
            _openHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _openHintTimer.Tick += (_, _) =>
            {
                _openHintTimer?.Stop();
                ClaudeOpenText.Text = Loc.T("claude.open");
            };
            _openHintTimer.Start();
            return;
        }
        // the island steps aside for the window that just came forward
        _clickOpened = false;
        _hoverActive = false;
        UpdateState();
    }

    private void ClaudeAllow_Click(object sender, RoutedEventArgs e) => AnswerAsk(e, allow: true);

    private void ClaudeDeny_Click(object sender, RoutedEventArgs e) => AnswerAsk(e, allow: false);

    private void AnswerAsk(RoutedEventArgs e, bool allow)
    {
        e.Handled = true;
        SoundService.Play(Sfx.Click);
        if (_claude.Active?.Ask is { } ask) _claude.Answer(ask, allow);
    }
}
