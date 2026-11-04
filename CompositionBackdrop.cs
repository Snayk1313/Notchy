using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Notchy;

/// <summary>
/// Windows.UI.Composition helpers: lets a classic Win32/WPF window host composition visuals,
/// which gives access to the system "host backdrop" (the same blurred background acrylic uses)
/// with an exact rounded clip and a real drop shadow.
/// </summary>
internal static class CompositionHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;    // DQTYPE_THREAD_CURRENT = 2
        public int apartmentType; // DQTAT_COM_STA = 2
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static IntPtr _controller;
    private static Compositor? _compositor;

    public static Compositor Compositor
    {
        get
        {
            if (_compositor != null) return _compositor;
            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
            {
                var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out _controller));
            }
            _compositor = new Compositor();
            return _compositor;
        }
    }

    public static unsafe DesktopWindowTarget CreateTarget(IntPtr hwnd, bool topmost)
    {
        IntPtr unk = ((WinRT.IWinRTObject)Compositor).NativeObject.ThisPtr;
        Guid iid = new("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out IntPtr interop));
        try
        {
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(*(void***)interop + 3));
            IntPtr target;
            Marshal.ThrowExceptionForHR(create(interop, hwnd, topmost ? 1 : 0, &target));
            var result = DesktopWindowTarget.FromAbi(target);
            Marshal.Release(target);
            return result;
        }
        finally
        {
            Marshal.Release(interop);
        }
    }
}

/// <summary>
/// One blurred rounded shape drawn by composition inside a host window, with a soft shadow.
/// The shadow is a stack of thin, very faint stroked bands that all start exactly at the shape's
/// edge and reach further and further out. Stacked, they form a smooth gaussian fall-off drawn only
/// OUTSIDE the shape: cheap to resize every frame, never cut off, and it doesn't darken the glass.
/// </summary>
internal sealed class BackdropShape
{
    private const int Rings = 20;
    private const float Spread = 34f;   // px the shadow reaches out
    private const float Sigma = 12.5f;  // gaussian softness
    private const float Overlap = 0.75f; // bands start slightly under the glass so there is never a gap

    private readonly ContainerVisual _container;
    private readonly ShapeVisual _shadowVisual;
    private readonly CompositionRoundedRectangleGeometry[] _ringGeoms = new CompositionRoundedRectangleGeometry[Rings];
    private readonly CompositionColorBrush[] _ringBrushes = new CompositionColorBrush[Rings];
    private readonly SpriteVisual _blur;
    private readonly SpriteVisual _tint;
    private readonly CompositionRoundedRectangleGeometry _clipGeom;
    private readonly CompositionColorBrush _tintBrush;
    private float _shadowStrength = 0.5f;
    private (float x, float y, float w, float h, float r) _last = (-1, -1, -1, -1, -1);

    public BackdropShape(ContainerVisual root, Vector2 hostSize)
    {
        var c = CompositionHost.Compositor;
        _container = c.CreateContainerVisual();
        root.Children.InsertAtTop(_container);

        _shadowVisual = c.CreateShapeVisual();
        _shadowVisual.Size = hostSize;
        for (int i = 0; i < Rings; i++)
        {
            _ringGeoms[i] = c.CreateRoundedRectangleGeometry();
            _ringBrushes[i] = c.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
            var ring = c.CreateSpriteShape(_ringGeoms[i]);
            ring.StrokeBrush = _ringBrushes[i];
            ring.StrokeThickness = Reach(i) + Overlap;
            _shadowVisual.Shapes.Add(ring);
        }
        _container.Children.InsertAtTop(_shadowVisual);
        ApplyShadowAlpha();

        _clipGeom = c.CreateRoundedRectangleGeometry();

        _blur = c.CreateSpriteVisual();
        _blur.Brush = c.CreateHostBackdropBrush();
        _blur.Clip = c.CreateGeometricClip(_clipGeom);
        _container.Children.InsertAtTop(_blur);

        _tintBrush = c.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        _tint = c.CreateSpriteVisual();
        _tint.Brush = _tintBrush;
        _tint.Clip = c.CreateGeometricClip(_clipGeom);
        _container.Children.InsertAtTop(_tint);
    }

    public bool Visible
    {
        set => _container.IsVisible = value;
    }

    public float Opacity
    {
        set => _container.Opacity = value;
    }

    public void SetHostSize(Vector2 size) => _shadowVisual.Size = size;

    /// <summary>0 = clear glass (no blur), 1 = full blur.</summary>
    public void SetBlurAmount(float amount)
    {
        _blur.Opacity = Math.Clamp(amount, 0f, 1f);
        _blur.IsVisible = amount > 0.001f;
    }

    public void SetTint(Color color) => _tintBrush.Color = color;

    public void SetShadow(bool on, float strength)
    {
        _shadowVisual.IsVisible = on;
        _shadowStrength = strength;
        ApplyShadowAlpha();
    }

    /// <summary>How far band i reaches outside the edge.</summary>
    private static float Reach(int i) => Spread * (i + 1) / Rings;

    private void ApplyShadowAlpha()
    {
        // Target darkness at distance d from the edge: A * exp(-(d/sigma)^2).
        // Band j covers [0, Reach(j)], so the area between Reach(j-1) and Reach(j) is covered by bands j..N-1.
        // Solve each band's alpha so the stacked result follows the curve exactly.
        double peak = 0.30 * _shadowStrength;
        double F(int j) => j >= Rings ? 0 : peak * Math.Exp(-Math.Pow((j == 0 ? 0 : Reach(j - 1)) / Sigma, 2));
        for (int j = 0; j < Rings; j++)
        {
            double a = 1 - (1 - F(j)) / (1 - F(j + 1));
            _ringBrushes[j].Color = Color.FromArgb((byte)Math.Clamp(Math.Round(a * 255), 0, 255), 0, 0, 0);
        }
    }

    /// <summary>Position/size in window pixels.</summary>
    public void Set(float x, float y, float w, float h, float radius)
    {
        var key = (x, y, w, h, radius);
        if (key == _last) return;
        _last = key;

        var size = new Vector2(Math.Max(0, w), Math.Max(0, h));
        var offset = new Vector3(x, y, 0);
        var r = new Vector2(radius, radius);

        _blur.Offset = offset;
        _blur.Size = size;
        _tint.Offset = offset;
        _tint.Size = size;
        _clipGeom.Size = size;
        _clipGeom.CornerRadius = r;

        for (int i = 0; i < Rings; i++)
        {
            // stroke covers [-Overlap, Reach] around the edge => its centre line sits at m outside the edge
            float m = (Reach(i) - Overlap) / 2f;
            var g = _ringGeoms[i];
            g.Offset = new Vector2(x - m, y - m);
            g.Size = new Vector2(Math.Max(0, w + m * 2), Math.Max(0, h + m * 2));
            float cr = Math.Max(0, radius + m);
            g.CornerRadius = new Vector2(cr, cr);
        }
    }
}
