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
    private const double ShotWidth = 252, ShotHeight = 48, ShotSeconds = 6;
    private static readonly Color ShotIce = Color.FromRgb(0x9A, 0xD7, 0xFF);

    private KeyboardHook? _captureHook;
    private CaptureWindow? _capture;          // the last one (kept hidden after a capture, for Editar)
    private bool _capturing;
    private BitmapSource? _shotPicture;
    private string? _shotFile;               // where it was saved, or a copy in the temp folder
    private Grid _shotPanel = null!;
    private Border _shotThumb = null!;
    private TextBlock _shotCaption = null!;
    private StackPanel _shotButtons = null!;
    private Border _shotCopy = null!, _shotSave = null!;
    private EyesFace _shotFace = null!;

    private bool ShotNoticeHolds => _shotPicture != null && _transient == CompactMode.Shot && DateTime.UtcNow < _transientUntil;

    private void InitCapture()
    {
        BuildShotPanel();
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
            if (_transient == CompactMode.Shot) EndShotNotice();
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
    // the notice on the island: eyes ^ ^, the picture, Copiar · Salvar · Editar
    // =====================================================================================

    private void BuildShotPanel()
    {
        _shotPanel = new Grid { Width = ShotWidth, Height = ShotHeight, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, IsHitTestVisible = false, Background = Brushes.Transparent };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _shotFace = new EyesFace { BaseMood = Mood.Happy, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_shotFace);
        _shotThumb = new Border { Width = 54, Height = 34, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = Loc.T("cap.open"),
            BorderBrush = White(0x22), BorderThickness = new Thickness(1) };
        _shotThumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_shotFile == null) return;
            SoundService.Play(Sfx.Click);
            try { Process.Start(new ProcessStartInfo(_shotFile) { UseShellExecute = true }); } catch (Exception ex) { App.Log(ex); }
            EndShotNotice();
        };
        row.Children.Add(_shotThumb);
        _shotCaption = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        row.Children.Add(_shotCaption);
        _shotButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        // only what's left to do: after Copiar, Salvar and Editar; after Salvar, Copiar and Editar
        _shotCopy = ShotButton("M7,5.5 H12 A1.5,1.5 0 0 1 13.5,7 V12 A1.5,1.5 0 0 1 12,13.5 H7 A1.5,1.5 0 0 1 5.5,12 V7 A1.5,1.5 0 0 1 7,5.5 Z M10.5,5.5 V3.5 A1,1 0 0 0 9.5,2.5 H3.5 A1,1 0 0 0 2.5,3.5 V9.5 A1,1 0 0 0 3.5,10.5 H5.5",
            Loc.T("cap.copy"), () =>
            {
                if (_shotPicture == null || !CaptureFiles.Copy(_shotPicture)) return;
                _shotCaption.Text = Loc.T("cap.copied");
                _shotCopy.Visibility = Visibility.Collapsed;
            });
        _shotSave = ShotButton("M8,2.5 V10.5 M4.5,7 L8,10.5 L11.5,7 M2.5,13.5 H13.5", Loc.T("cap.save.short"), () =>
        {
            if (_shotPicture == null) return;
            _transientUntil = DateTime.UtcNow.AddSeconds(60); // (stays while the folder window is open)
            var file = CaptureFiles.Save(_shotPicture, this);
            _transientUntil = DateTime.UtcNow.AddSeconds(ShotSeconds);
            if (file == null) return;
            _shotFile = file;
            _shotCaption.Text = Loc.T("cap.saved");
            _shotSave.Visibility = Visibility.Collapsed;
        });
        _shotButtons.Children.Add(_shotCopy);
        _shotButtons.Children.Add(_shotSave);
        _shotButtons.Children.Add(ShotButton("M2.5,13.5 L3.5,10 L10.5,3 L13,5.5 L6,12.5 Z", Loc.T("cap.edit"), () =>
        {
            if (_capture == null) return;
            EndShotNotice();
            _capturing = true;
            Opacity = 0;
            _capture.Reopen();
        }));
        row.Children.Add(_shotButtons);
        _shotPanel.Children.Add(row);
        ((Panel)CompactPanel.Parent).Children.Add(_shotPanel);
    }

    private Border ShotButton(string icon, string tip, Action click)
    {
        var b = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = White(0x1C), Margin = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip,
            Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Stroke = Brushes.White, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, RenderTransform = new ScaleTransform(0.78, 0.78, 8, 8) }
        };
        b.MouseEnter += (_, _) => b.Background = White(0x30);
        b.MouseLeave += (_, _) => b.Background = White(0x1C);
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
        _shotPicture = picture;
        _shotFile = file;
        _shotThumb.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        // the picture that went in comes out as the thumbnail: it grows inside the opening island
        var grow = new ScaleTransform(0.3, 0.3);
        _shotThumb.RenderTransformOrigin = new Point(0.5, 0.5);
        _shotThumb.RenderTransform = grow;
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(Dur(420))) { BeginTime = TimeSpan.FromMilliseconds(Dur(110)), EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _shotThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(200))) { BeginTime = TimeSpan.FromMilliseconds(Dur(110)) });
        _shotCaption.Text = caption;
        _shotButtons.Visibility = done == null ? Visibility.Collapsed : Visibility.Visible;
        _shotCopy.Visibility = done == CaptureAction.Copy ? Visibility.Collapsed : Visibility.Visible;
        _shotSave.Visibility = done == CaptureAction.Save ? Visibility.Collapsed : Visibility.Visible;
        _shotFace.React(Mood.Happy, 2);
        _shotLightAt = AnimNow;
        ShowTransient(CompactMode.Shot, "", "", ShotSeconds);
    }

    private void EndShotNotice()
    {
        _shotPicture = null;
        if (_transient == CompactMode.Shot) _transient = CompactMode.None;
        UpdateState();
    }

    private double _shotLightAt = -10, _shotFaceLast;

    /// <summary>Every frame: the notice stays while the mouse is on it, and goes when its time is up.</summary>
    private void UpdateShotNotice()
    {
        if (_shotPicture == null) return;
        if (_transient != CompactMode.Shot)
        {
            _shotPicture = null;
            return;
        }
        if (_shotPanel.IsMouseOver) _transientUntil = DateTime.UtcNow.AddSeconds(1.5);
        double now = _faceClock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _shotFaceLast, 0, 0.1);
        _shotFaceLast = now;
        _shotFace.Update(dt, 0);
        if (DateTime.UtcNow >= _transientUntil) EndShotNotice();
    }
}
