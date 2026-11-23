using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// Notchy's screen capture: Print Screen (or the shortcut in Configurações) opens <see cref="CaptureWindow"/>
/// instead of Windows' tool. The island hides while the screen is frozen (so it's not in the picture) and the
/// toolbar comes from where it is; after Copiar / Salvar / Ler texto the picture flies up to it, and the island
/// shows it for a moment with Copiar, Salvar and Editar.
/// </summary>
public partial class MainWindow
{
    private const double ShotThumbWidth = 86, ShotThumbHeight = 54;
    private static readonly Color ShotIce = Color.FromRgb(0x9A, 0xD7, 0xFF);

    private KeyboardHook? _captureHook;
    private CaptureWindow? _capture;          // the last one (kept hidden after a capture, for Editar)
    private bool _capturing;
    private StackPanel _shotButtons = null!;
    private Border _shotCopy = null!, _shotSave = null!;

    /// <summary>A screenshot's card: where the picture is (saved, or a copy in the temp folder) and what was already done with it.</summary>
    private sealed class ShotCard
    {
        public string? File;
        public CaptureAction? Done;
    }

    /// <summary>The screenshot card on screen now, if that's what the notification card shows.</summary>
    private ShotCard? ShotShown => _noticeShown?.Local as ShotCard;
    private BitmapSource? ShotPicture => ShotShown != null ? _noticeShown!.Avatar : null;

    private void InitCapture()
    {
        BuildShotButtons();
        SetCaptureHotkey(S.CaptureHotkey);
    }

    /// <summary>"Snapshot" = the Print Screen key alone; "" = none.</summary>
    public void SetCaptureHotkey(string name)
    {
        _captureHook?.Dispose();
        _captureHook = null;
        if (string.IsNullOrEmpty(name)) return;
        uint wantMods;
        int wantVk;
        if (KeyboardHook.ParseBare(name) is int bare)
        {
            wantMods = 0;
            wantVk = bare;
        }
        else if (KeyboardHook.Parse(name) is { } k)
        {
            wantMods = k.mods;
            wantVk = (int)k.vk;
        }
        else return;
        bool swallowUp = false;
        _captureHook = new KeyboardHook((vk, down) =>
        {
            if (vk != wantVk) return false;
            if (!down)
            {
                bool was = swallowUp;
                swallowUp = false;
                return was; // (Windows opens its own tool on the key going up)
            }
            if (HeldMods() != wantMods) return false; // (Win + Print Screen and the like stay Windows')
            swallowUp = true;
            Dispatcher.BeginInvoke(() => _ = StartCapture());
            return true;
        });
    }

    private static uint HeldMods()
    {
        uint m = 0;
        if (GetAsyncKeyStateShift(0x5B) < 0 || GetAsyncKeyStateShift(0x5C) < 0) m |= KeyboardHook.ModWin;
        if (GetAsyncKeyStateShift(0x11) < 0) m |= KeyboardHook.ModCtrl;
        if (GetAsyncKeyStateShift(0x12) < 0) m |= KeyboardHook.ModAlt;
        if (GetAsyncKeyStateShift(0x10) < 0) m |= KeyboardHook.ModShift;
        return m;
    }

    public async Task StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            _capture?.Close();
            _capture = null;
            if (ShotShown != null) DismissNotice();
            // where the island is (the toolbar comes from there, the picture goes back there)
            var mid = Island.PointToScreen(new Point(Island.ActualWidth / 2, _topHidden + 17));
            double width = Island.PointToScreen(new Point(Island.ActualWidth, 0)).X - Island.PointToScreen(new Point(0, 0)).X;
            // the screen is frozen with the island on it (it shows in the picture, as in any capture app); then the
            // live one steps aside, so it never covers the frozen screen or the toolbar
            var w = new CaptureWindow(mid, width);
            // the same spot without the island (Windows leaves it out of this picture, nothing blinks on screen):
            // when the real island comes back over the capture, the frozen one mustn't show around it
            Opacity = 0;
            w.Done += OnCaptureDone;
            // as the picture leaves for the island, the real island steps in over the frozen one (same place, same look):
            // the picture flies into it, and it's this same island that then opens into the notice
            w.Returning += () =>
            {
                _quietClipUntil = DateTime.UtcNow.AddSeconds(2); // (the copy it just made comes with its own light)
                _capturing = false;
                Opacity = 1;
                Native.BringTopmost(_hwnd);
                if (_backdrop != null && _backdrop.Hwnd != IntPtr.Zero) _backdrop.KeepBelow(_hwnd);
            };
            w.Cancelled += () =>
            {
                _capture = null;
                EndCapture();
            };
            _capture = w;
            w.Show();
            // the keyboard too, at once: Esc must cancel before anything is clicked
            Native.FocusWindow(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            w.Activate();
            w.Focus();
            // the same spot without the island, taken once the capture covers the screen (so nothing changes on screen):
            // when the real island comes back over the capture, the frozen one mustn't show around it
            if (await IslandPatch(w) is var (patch, rect)) w.SetIslandPatch(patch, rect);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            EndCapture();
        }
    }

    /// <summary>
    /// The screen around the island without it: taken right after the capture window covers the screen, with that
    /// window left out of the picture by Windows (the island and its glass are already hidden behind it). Nothing on
    /// screen changes, and the island's glass window is never touched (leaving it out of a picture froze its shape).
    /// </summary>
    private async Task<(BitmapSource, Int32Rect)?> IslandPatch(Window capture)
    {
        try
        {
            var a = Island.PointToScreen(new Point(0, 0));
            var b = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
            var r = new System.Drawing.Rectangle((int)a.X - 40, Math.Max((int)a.Y, 0), (int)(b.X - a.X) + 80, (int)(b.Y - Math.Max(a.Y, 0)) + 40);
            await Task.Delay(60); // (the island and its glass gone from the screen under the capture)
            var hwnd = new System.Windows.Interop.WindowInteropHelper(capture).Handle;
            SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
            try
            {
                await Task.Delay(40);
                using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                IntPtr hb = bmp.GetHbitmap();
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return (src, new Int32Rect(r.Left, r.Top, r.Width, r.Height));
                }
                finally
                {
                    DeleteObject(hb);
                }
            }
            finally
            {
                SetWindowDisplayAffinity(hwnd, 0);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private void EndCapture()
    {
        _capturing = false;
        Opacity = 1;
    }

    private void OnCaptureDone(CaptureAction action, BitmapSource picture, Int32Rect at, string? file)
    {
        EndCapture();
        switch (action)
        {
            case CaptureAction.Pin:
                new PinWindow(picture, at, p => CaptureFiles.Copy(p), (p, owner) => CaptureFiles.Save(p, owner)).Show();
                return;
            case CaptureAction.Copy:
                ShowShotNotice(picture, Loc.T("cap.copied"), TempFile(picture), CaptureAction.Copy);
                return;
            case CaptureAction.Save:
                ShowShotNotice(picture, Loc.T("cap.saved"), file ?? TempFile(picture), CaptureAction.Save);
                return;
            case CaptureAction.Ocr:
                _ = ReadPictureText(picture);
                return;
        }
    }

    /// <summary>A copy in the temp folder, so clicking the notice can open it (and the text reader can read it).</summary>
    private static string? TempFile(BitmapSource picture)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Notchy", "captures");
            if (Directory.Exists(dir))
                foreach (var old in Directory.GetFiles(dir, "*.png"))
                    if (File.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
            string file = System.IO.Path.Combine(dir, $"captura-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            CaptureFiles.Write(picture, file);
            return file;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private async Task ReadPictureText(BitmapSource picture)
    {
        string? file = TempFile(picture);
        string? text = null;
        try
        {
            if (file != null) text = await ClipboardService.ReadTextAsync(file);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        ShowShotNotice(picture, Loc.T(string.IsNullOrWhiteSpace(text) ? "cap.notext" : "cap.textcopied"), file, null);
    }

    // =====================================================================================
    // the card: the picture, "Print · agora", what was done, Copiar · Salvar · Editar as round icons. It's the
    // notifications' card, in their line, under the island: the island itself stays free to open.
    // =====================================================================================

    private void BuildShotButtons()
    {
        _shotButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        // only what's left to do: after Copiar, Salvar and Editar; after Salvar, Copiar and Editar
        _shotCopy = ShotButton("M7,5.5 H12 A1.5,1.5 0 0 1 13.5,7 V12 A1.5,1.5 0 0 1 12,13.5 H7 A1.5,1.5 0 0 1 5.5,12 V7 A1.5,1.5 0 0 1 7,5.5 Z M10.5,5.5 V3.5 A1,1 0 0 0 9.5,2.5 H3.5 A1,1 0 0 0 2.5,3.5 V9.5 A1,1 0 0 0 3.5,10.5 H5.5",
            Loc.T("cap.copy"), () =>
            {
                if (ShotPicture is not { } picture || !CaptureFiles.Copy(picture)) return;
                _pillTitle.Text = Loc.T("cap.copied");
                _shotCopy.Visibility = Visibility.Collapsed;
            });
        _shotSave = ShotButton("M8,2.5 V10.5 M4.5,7 L8,10.5 L11.5,7 M2.5,13.5 H13.5", Loc.T("cap.save.short"), () =>
        {
            if (ShotShown is not { } shot || ShotPicture is not { } picture) return;
            _pillUntil = DateTime.UtcNow.AddSeconds(60); // (stays while the folder window is open)
            var file = CaptureFiles.Save(picture, this);
            _pillUntil = DateTime.UtcNow.AddSeconds(NoticeSeconds);
            if (file == null) return;
            shot.File = file;
            _pillTitle.Text = Loc.T("cap.saved");
            _shotSave.Visibility = Visibility.Collapsed;
        });
        _shotButtons.Children.Add(_shotCopy);
        _shotButtons.Children.Add(_shotSave);
        _shotButtons.Children.Add(ShotButton("M2.5,13.5 L3.5,10 L10.5,3 L13,5.5 L6,12.5 Z", Loc.T("cap.edit"), () =>
        {
            if (_capture == null) return;
            DismissNotice();
            _capturing = true;
            Opacity = 0;
            _capture.Reopen();
        }));
        Grid.SetColumn(_shotButtons, 2);
        _pillRow.Children.Add(_shotButtons);
    }

    private Border ShotButton(string icon, string tip, Action click)
    {
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = White(0x1C), Margin = new Thickness(5, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip,
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Stroke = Brushes.White, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, RenderTransform = new ScaleTransform(0.78, 0.78, 8, 8) }
        };
        b.MouseEnter += (_, _) => b.Background = White(0x30);
        b.MouseLeave += (_, _) => b.Background = White(0x1C);
        b.MouseLeftButtonDown += (_, e) => e.Handled = true; // (not a click on the card, nor the start of a swipe)
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return b;
    }

    /// <param name="done">What was already done (its button doesn't show); null: no buttons (the text was read).</param>
    private void ShowShotNotice(BitmapSource picture, string caption, string? file, CaptureAction? done)
    {
        Happy(); // the island's eyes ^ ^ (the card has none: one pair of eyes on screen)
        var n = new Notice(0, Loc.T("cap.card"), "", caption, $"{picture.PixelWidth} × {picture.PixelHeight}", DateTime.Now, picture, false)
        {
            Accent = ShotIce,
            Local = new ShotCard { File = file, Done = done }
        };
        _noticeQueue.Enqueue(n); // (in line with the notifications, in the order they came)
        if (_noticeShown == null) ShowNextNotice();
        else UpdateNoticeCount();
    }

    /// <summary>Its turn in the line: the card under the island, the picture that went in growing out as its thumbnail.</summary>
    private void ShowShotCard(Notice n, ShotCard shot)
    {
        _shotButtons.Visibility = shot.Done == null ? Visibility.Collapsed : Visibility.Visible;
        _shotCopy.Visibility = shot.Done == CaptureAction.Copy ? Visibility.Collapsed : Visibility.Visible;
        _shotSave.Visibility = shot.Done == CaptureAction.Save ? Visibility.Collapsed : Visibility.Visible;
        ShowNoticePill(n);
        var grow = new ScaleTransform(0.3, 0.3);
        _pillAvatar.RenderTransformOrigin = new Point(0.5, 0.5);
        _pillAvatar.RenderTransform = grow;
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(Dur(420))) { BeginTime = TimeSpan.FromMilliseconds(Dur(110)), EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>A click on the card: the picture, in the default viewer.</summary>
    private static void OpenShot(ShotCard shot)
    {
        if (shot.File == null) return;
        try { Process.Start(new ProcessStartInfo(shot.File) { UseShellExecute = true }); } catch (Exception ex) { App.Log(ex); }
    }
}
