using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WowLauncher.Controls;

/// <summary>
/// The v2 backdrop: one hearth, rendered as a coarse print halftone that falls off into darkness.
/// Texture, not illustration — we may not ship a single pixel of the game's own artwork (CLAUDE.md §10),
/// so the atmosphere is generated instead of licensed.
///
/// The dots inside the glow breathe on independent phases, so it reads as embers rather than as a
/// printed still. The cold dots outside the glow are baked once into a bitmap and blitted; redrawing
/// all ~11k of them every frame would burn CPU for no visible gain.
/// </summary>
public sealed class EmberField : Control
{
    /// <summary>False when the realm is down — the fire is all but out.</summary>
    public static readonly StyledProperty<bool> LitProperty =
        AvaloniaProperty.Register<EmberField, bool>(nameof(Lit), true);

    public bool Lit
    {
        get => GetValue(LitProperty);
        set => SetValue(LitProperty, value);
    }

    private const double Step = 7;          // halftone screen pitch
    private const double EmberCut = 0.10;   // above this value a dot is alive

    private readonly record struct Ember(double X, double Y, double R, double T, double Phase, double Speed);

    private readonly List<Ember> _embers = new();
    private RenderTargetBitmap? _cold;      // the static part of the screen
    private Size _built;
    private DispatcherTimer? _timer;
    private double _clock;
    private readonly bool _reducedMotion = !SystemAnimationsEnabled();

    static EmberField()
    {
        AffectsRender<EmberField>(LitProperty);
        LitProperty.Changed.AddClassHandler<EmberField>((f, _) => f.Invalidate());
    }

    // Klassisches DllImport statt [LibraryImport]: letzteres verlangt <AllowUnsafeBlocks> im ganzen
    // Projekt (SYSLIB1062) — fuer das Lesen eines einzigen BOOL kein hinreichender Grund.
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfoW",
        SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>
    /// Avalonia exposes no reduced-motion flag, so we ask Windows directly:
    /// SPI_GETCLIENTAREAANIMATION (0x1042) reports the user's "Show animations in Windows"
    /// setting — TRUE means animations are wanted. If the call fails, we keep the fire breathing.
    /// </summary>
    private static bool SystemAnimationsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        int enabled = 1;
        return !SystemParametersInfoW(0x1042, 0, ref enabled, 0) || enabled != 0;
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // The user turned animations off system-wide — the hearth stays still (a11y).
        if (_reducedMotion) return;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            _clock += 0.05;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _cold?.Dispose();
        _cold = null;
        _built = default;
        base.OnUnloaded(e);
    }

    private void Invalidate()
    {
        _built = default;   // force a rebuild: the hearth changed size or went out
        InvalidateVisual();
    }

    /// <summary>Deterministic noise — the same field every launch, so screenshots are stable.</summary>
    private static Func<double> Rng(int seed)
    {
        var s = seed;
        return () =>
        {
            s = unchecked(s * 1103515245 + 12345) & 0x7fffffff;
            return s / (double)0x7fffffff;
        };
    }

    private void Build(Size size)
    {
        _embers.Clear();
        _cold?.Dispose();
        _cold = null;

        var w = size.Width;
        var h = size.Height;
        if (w < 1 || h < 1) return;

        var rnd = Rng(1337);

        // The hearth sits low and off to the right, wide and flat — floor glow, not a blob in the air.
        // The type lives bottom-left, so that corner stays dark on purpose.
        var srcX = w * 0.72;
        var srcY = h * 1.02;
        var r = Lit ? h * 1.55 : h * 0.80;
        var alpha = Lit ? 0.78 : 0.40;

        _cold = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w), (int)Math.Ceiling(h)), new Vector(96, 96));
        using (var ctx = _cold.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x11, 0x0F)), new Rect(0, 0, w, h));

            for (var y = Step / 2; y < h; y += Step)
                for (var x = Step / 2; x < w; x += Step)
                {
                    var dx = (x - srcX) / (r * 0.78);
                    var dy = (y - srcY) / (r * 0.52);

                    var v = 1 - Math.Sqrt(dx * dx + dy * dy);
                    v += (rnd() - 0.5) * 0.13;
                    v = Math.Pow(Math.Clamp(v, 0, 1), 2.4);

                    var dot = Math.Max(0.55, v * (Step * 0.5));   // the screen never fully disappears
                    var t = Math.Min(1, v * 1.15);

                    if (t > EmberCut)
                    {
                        _embers.Add(new Ember(x, y, dot, t, rnd() * 6.28, 0.5 + rnd() * 0.9));
                    }
                    else
                    {
                        ctx.DrawEllipse(DotBrush(t, alpha), null, new Point(x, y), dot, dot);
                    }
                }
        }

        _built = size;
    }

    /// <summary>Duotone ramp: basalt shadow → ember highlight. No other colour survives.</summary>
    private static IBrush DotBrush(double t, double alpha)
    {
        var cr = (byte)Math.Round(38 + t * (217 - 38));
        var cg = (byte)Math.Round(32 + t * (117 - 32));
        var cb = (byte)Math.Round(28 + t * (43 - 28));
        var a = (byte)Math.Round(255 * alpha * (0.30 + 0.70 * t));
        return new SolidColorBrush(Color.FromArgb(a, cr, cg, cb));
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;

        if (size != _built) Build(size);
        if (_cold is null) return;

        ctx.DrawImage(_cold, new Rect(size));

        var alpha = Lit ? 0.78 : 0.40;
        foreach (var e in _embers)
        {
            // two detuned sines — organic, never a metronome
            var puls = _reducedMotion
                ? 1
                : 1 + 0.22 * Math.Sin(_clock * e.Speed + e.Phase)
                    + 0.09 * Math.Sin(_clock * e.Speed * 2.3 + e.Phase * 1.7);

            var t = Math.Clamp(e.T * puls, 0, 1);
            var rr = Math.Max(0.55, e.R * (0.88 + 0.12 * puls));
            ctx.DrawEllipse(DotBrush(t, alpha), null, new Point(e.X, e.Y), rr, rr);
        }
    }
}
