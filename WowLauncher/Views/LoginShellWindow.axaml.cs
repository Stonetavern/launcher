using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WowLauncher.Startup;
using WowLauncher.ViewModels;

namespace WowLauncher.Views;

/// <summary>
/// The 1.9 login/loading shell. View-only mechanics live here, nothing else: the motion gate, the
/// first-frame measurement, the off-thread backdrop decode, the error shake, the success pulse, the
/// Phase-3 sigil travel and the storyboard clock. All state is <see cref="LoginShellViewModel"/>;
/// <see cref="App"/> opens and closes this window and shows the shell when
/// <see cref="LoginShellViewModel.TransitionCompleted"/> fires.
///
/// <para>Every duration and distance used from code is read from the Motion.axaml tokens
/// (<see cref="Token{T}"/>), never typed here: MotionTokenTests greps this file for literals.</para>
/// </summary>
public partial class LoginShellWindow : Window
{
    private LoginShellViewModel? _vm;
    private bool _firstFrameSeen;
    private bool _focusedOnce;
    private int _lastShakeTick;

    /// <summary>QA-only: keep the window in Phase 0 after the first frame (the <c>--state cold</c>
    /// still). Never set on an interactive run.</summary>
    public bool HoldAtPhase0 { get; set; }

    public LoginShellWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Same reason as the shells: KWin draws a bright active frame at BorderOnly, and the declared
        // None is what Windows already gets. macOS keeps its native frame.
        if (OperatingSystem.IsLinux())
            WindowDecorations = WindowDecorations.None;

        UpdateMotion();
        DataContextChanged += (_, _) => Attach(DataContext as LoginShellViewModel);
        Opened += OnOpened;
        Closing += (_, _) => _vm?.Shutdown();
    }

    // ── Motion gate (same wiring as SplashWindow / ShellV3Window) ─────────────────────────────────

    private void UpdateMotion()
    {
        var live = IsVisible && WindowState != WindowState.Minimized;
        if (this.FindControl<Border>("Root") is { } root)
            root.Classes.Set("live", live);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
            UpdateMotion();
    }

    /// <summary>Raised once, when the window's first frame is really on screen. The self-update health
    /// contract is answered here: this is the moment the new build has proven it can start.</summary>
    public event Action? FirstFrameRendered;

    // ── Phase 0 -> 1: the first frame ──────────────────────────────────────────────────────────────

    /// <summary>
    /// After the window opened, wait for the FIRST RENDERED FRAME, then hand over to Phase 1. That
    /// is the one guarantee of Spec §4: nothing decoded, nothing fetched before a frame is on screen.
    /// The elapsed time is logged against <see cref="StartupClock"/> (the number the 800 ms budget
    /// is judged by).
    /// </summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        RequestAnimationFrame(_frameTime =>
        {
            if (_firstFrameSeen) return;
            _firstFrameSeen = true;

            var ms = StartupClock.Elapsed.TotalMilliseconds;
            Serilog.Log.Information("Login shell: first frame rendered at {Ms:F0} ms after process start", ms);
            Console.Error.WriteLine($"[login-shell] first frame at {ms:F0} ms");
            FirstFrameRendered?.Invoke();

            if (HoldAtPhase0) return;
            _vm?.BeginPhase1();
            _ = DecodeBackdropAsync();
        });
    }

    /// <summary>Decode the backdrop off the UI thread; the gradient stays until it is ready. A decode
    /// failure keeps the gradient and logs, never dialogs (§4).</summary>
    private async Task DecodeBackdropAsync()
    {
        try
        {
            var uri = new Uri("avares://WowLauncher/Assets/Backgrounds/hero-stonetavern.webp");
            var bitmap = await Task.Run(() =>
            {
                using var stream = AssetLoader.Open(uri);
                return new Bitmap(stream);
            }).ConfigureAwait(true);

            if (this.FindControl<Image>("Backdrop") is { } image)
            {
                image.Source = bitmap;
                image.Classes.Add("ready");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Login shell: backdrop decode failed, keeping the gradient");
        }
    }

    // ── ViewModel wiring ───────────────────────────────────────────────────────────────────────────

    private void Attach(LoginShellViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(LoginShellViewModel.ShakeTick) when _vm.ShakeTick != _lastShakeTick:
                _lastShakeTick = _vm.ShakeTick;
                _ = ShakeCardAsync();
                break;

            case nameof(LoginShellViewModel.WantsPasswordFocus) when _vm.WantsPasswordFocus:
                _vm.WantsPasswordFocus = false;
                this.FindControl<TextBox>("PasswordBox")?.Focus();
                break;

            case nameof(LoginShellViewModel.Phase):
                OnPhaseChanged(_vm.Phase);
                break;
        }
    }

    private void OnPhaseChanged(LoginPhase phase)
    {
        switch (phase)
        {
            case LoginPhase.Login when !_focusedOnce:
                // First entry into Phase 1: the account field takes the focus so a player can type
                // at once, before the pipeline is anywhere near done (§5.2: never blocks input).
                // Only the first time: after a rejected attempt the focus goes to the password
                // field (WantsPasswordFocus), and this must not steal it back.
                _focusedOnce = true;
                Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("UsernameBox")?.Focus(),
                                         DispatcherPriority.Input);
                break;

            case LoginPhase.Succeeded:
                _ = PulseThenTransitionAsync();
                break;

            case LoginPhase.Transition:
                RunStoryboard();
                break;
        }
    }

    // ── §6 Erfolg: one stronger beat, then Phase 3 ─────────────────────────────────────────────────

    private async Task PulseThenTransitionAsync()
    {
        try
        {
            if (this.FindControl<Border>("SuccessBloom") is { } bloom)
            {
                var pulse = new Animation
                {
                    Duration = Token<TimeSpan>("Motion.Base"),
                    Easing = Token<Easing>("Ease.Standard"),
                    Children =
                    {
                        new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(OpacityProperty, 0.0) } },
                        new KeyFrame { Cue = new Cue(0.4), Setters = { new Setter(OpacityProperty, 1.0) } },
                        new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(OpacityProperty, 0.0) } },
                    },
                };
                await pulse.RunAsync(bloom).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Login shell: success pulse skipped");
        }
        _vm?.BeginTransition();
    }

    // ── §6 Fehler: the card shake, from the tokens ────────────────────────────────────────────────

    /// <summary>Amplitude, cycle count and duration come from Motion.axaml; the keyframes are derived
    /// (+A, -A per cycle, settle at 0), so the sheet stays the only place the numbers live.</summary>
    private async Task ShakeCardAsync()
    {
        if (this.FindControl<Panel>("CardShaker") is not { } card) return;

        var amplitude = Token<double>("Motion.ShakeAmplitude");
        var cycles = Token<int>("Motion.ShakeCycles");
        var shake = new Animation
        {
            Duration = Token<TimeSpan>("Motion.Slow"),
            Easing = Token<Easing>("Ease.Standard"),
        };

        // 2 cycles -> cues 0, .2, .4, .6, .8, 1 with values 0, +A, -A, +A, -A, 0.
        var steps = cycles * 2;
        shake.Children.Add(new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } });
        for (var i = 1; i <= steps; i++)
        {
            var value = (i % 2 == 1) ? amplitude : -amplitude;
            shake.Children.Add(new KeyFrame
            {
                Cue = new Cue((double)i / (steps + 1)),
                Setters = { new Setter(TranslateTransform.XProperty, value) },
            });
        }
        shake.Children.Add(new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(TranslateTransform.XProperty, 0.0) } });

        try
        {
            // The wrapper carries a plain TranslateTransform (markup); the animation targets its X.
            await shake.RunAsync(card).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Login shell: shake skipped");
        }
    }

    // ── §7 Phase 3: the storyboard ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Five movements at once, all on Motion.Transition (the styles under "Border.leaving" carry
    /// card, dimmer, chrome and status; the sigil's travel is computed here from real bounds). The
    /// clock is the same token: when it has run, the ViewModel completes the phase and App shows
    /// the shell.
    /// </summary>
    private void RunStoryboard()
    {
        if (this.FindControl<Panel>("Sigil") is { } sigil
            && this.FindControl<Image>("SigilMark") is { } mark
            && this.FindControl<Panel>("ChromeSigilSlot") is { } slot
            && this is Visual root)
        {
            var from = sigil.TranslatePoint(new Point(sigil.Bounds.Width / 2, sigil.Bounds.Height / 2), root);
            var to = slot.TranslatePoint(new Point(slot.Bounds.Width / 2, slot.Bounds.Height / 2), root);
            if (from is { } a && to is { } b && mark.Bounds.Height > 0)
            {
                // The MARK (lantern) arrives at the slot's height; ring and bloom fade out on the way
                // (styles under "leaving"), so what lands in the chrome is the lantern alone.
                var scale = slot.Bounds.Height / mark.Bounds.Height;
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                // Order matters: TransformOperations applies left to right, so scale first and the
                // translation stays in window pixels (a translate before the scale gets scaled too:
                // measured 2026-09-20, the lantern landed a fifth of the way).
                sigil.RenderTransform = TransformOperations.Parse(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"scale({scale:F3}) translate({dx:F1}px,{dy:F1}px)"));
            }
        }

        DispatcherTimer.RunOnce(() => _vm?.CompleteTransition(), Token<TimeSpan>("Motion.Transition"));
    }

    // ── Window mechanics ──────────────────────────────────────────────────────────────────────────

    private void OnDragStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── Tokens ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A Motion.axaml resource, resolved through the window's resource chain (the sheet is
    /// merged at App level). Throws on a missing key: a typo here must not silently animate with a
    /// default.</summary>
    private T Token<T>(string key)
    {
        if (this.TryFindResource(key, out var value) && value is T typed) return typed;
        throw new InvalidOperationException($"Motion token {key} not found or not a {typeof(T).Name}");
    }
}
