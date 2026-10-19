using Microsoft.Gaming.XboxGameBar;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace GHelper.GameBar
{
    /// <summary>
    /// The G-Helper widget shown in the Game Bar home bar: a gamepad-first control surface
    /// for the ROG Ally, with performance modes and live telemetry underneath.
    ///
    /// Two things make it feel alive rather than like a settings dump:
    ///
    ///  * Changes are applied optimistically. Pressing a control highlights it straight away
    ///    and the highlight is only confirmed once G-Helper has actually applied it, so a
    ///    rejected change visibly falls back rather than lying.
    ///  * Everything live is re-read on a timer, so the gauges, the frame rate and the
    ///    "now playing" line track the machine without the user doing anything.
    /// </summary>
    public sealed partial class GHelperWidget : Page
    {
        private const int PollIntervalMs = 1000;

        /// <summary>Single wording per state, so there is one string to translate later.</summary>
        private const string StatusConnected = "connected";
        private const string StatusOffline = "G-Helper not running";

        // The Game Bar widget object, held as object so that the SDK's type is not resolved
        // when this page's type is loaded. WidgetHost does the typed work.
        private object widget;
        private IDisposable widgetSubscription;

        // Shared with the settings widget: both pages live in this process and the desktop
        // server serves one client at a time.
        private readonly GHelperClient client = GHelperClient.Shared;

        private DispatcherTimer timer;
        private System.Threading.CancellationTokenSource lifetime;

        /// <summary>Re-entrancy guard: a slow tick must not overlap the next one.</summary>
        private bool busy;

        /// <summary>Key of the in-flight command, so its control shows as pending.</summary>
        private string pendingKey;

        /// <summary>Strips are rebuilt only when the option list changes.</summary>
        private string modeSignature;
        private string gpuSignature;
        private string screenSignature;
        private string fpsSignature;
        private string controllerSignature;

        private string currentMode = "";
        private string currentGpuMode = "";
        private string currentScreenMode = "";
        private string currentFpsCap = "";
        private string currentControllerMode = "";
        private string currentVibration = "";
        private bool currentFnLock;
        private bool currentOverlay;
        private bool currentBatteryFull;
        private bool currentAutoTdp;
        private bool ally;

        public GHelperWidget()
        {
            InitializeComponent();
            ApplyPalette();
            SetOffline();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            widget = e.Parameter;
            if (widget != null)
            {
                widgetSubscription = WidgetHost.Subscribe(widget, ApplyTheme, OnCompactModeChanged);
                ApplyTheme();
            }

            lifetime = new System.Threading.CancellationTokenSource();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
            timer.Tick += TimerTick;
            timer.Start();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= TimerTick;
                timer = null;
            }

            if (lifetime != null)
            {
                lifetime.Cancel();
                lifetime.Dispose();
                lifetime = null;
            }

            if (widget != null)
            {
                widgetSubscription?.Dispose();
                widget = null;
            }

            base.OnNavigatedFrom(e);
        }

        private async void TimerTick(object sender, object e)
        {
            if (busy || lifetime == null || lifetime.IsCancellationRequested) return;

            busy = true;
            try
            {
                await RefreshAsync(lifetime.Token);
            }
            catch (Exception)
            {
                SetOffline();
            }
            finally
            {
                busy = false;
            }
        }

        private async Task RefreshAsync(System.Threading.CancellationToken cancellationToken)
        {
            Dictionary<string, string> state = await client.GetStateAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            SetOnline();
            Apply(state);
        }

        // ── Status ─────────────────────────────────────────────────────────

        private void SetOnline()
        {
            dotStatus.Fill = BrushAccent;
            panelContent.Opacity = 1.0;
        }

        private void SetOffline()
        {
            dotStatus.Fill = BrushWarn;
            panelContent.Opacity = 0.45;
            txtModel.Text = StatusOffline;
            txtFocusedApp.Text = "";
            txtFps.Text = "--";
            txtCpuTemp.Text = "";
            txtGpuTemp.Text = "";
            txtMemLoad.Text = "";
            txtTdp.Text = "--";
        }

        /// <summary>
        /// Applies the light or dark palette.
        ///
        /// The brushes are named page resources, so re-colouring them re-styles every
        /// element that uses them at once - including the buttons this page creates at
        /// runtime. UWP has no FindResource, so the brushes are held as fields instead.
        /// </summary>
        private void ApplyPalette()
        {
            bool dark = Root.RequestedTheme != Windows.UI.Xaml.ElementTheme.Light;

            BrushCard.Color = dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0x00, 0x00, 0x00);
            BrushCardStroke.Color = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            BrushSubtleText.Color = dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8A, 0x00, 0x00, 0x00);
            BrushFaintText.Color = dark ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0x00, 0x00, 0x00);
            BrushAccent.Color = dark ? Color.FromArgb(0xFF, 0x5F, 0xD3, 0x5F) : Color.FromArgb(0xFF, 0x1E, 0x8B, 0x2E);
            BrushAccentSoft.Color = dark ? Color.FromArgb(0x33, 0x5F, 0xD3, 0x5F) : Color.FromArgb(0x22, 0x1E, 0x8B, 0x2E);
            BrushAccentText.Color = dark ? Color.FromArgb(0xFF, 0x0B, 0x1A, 0x0B) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            BrushIdleText.Color = dark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            BrushWarn.Color = dark ? Color.FromArgb(0xFF, 0xC4, 0x6B, 0x00) : Color.FromArgb(0xFF, 0x8A, 0x5A, 0x00);
            BrushTrack.Color = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
            BrushIdleButton.Color = dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);

            RestyleAll();
        }

        // ── State ───────────────────────────────────────────────────────────

        private void Apply(Dictionary<string, string> state)
        {
            ally = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyIsAlly, false);

            txtModel.Text = GameBarProtocol.ReadString(state, GameBarProtocol.KeyModel, "G-Helper");
            txtPowerSource.Text = PowerLabel(GameBarProtocol.ReadString(state, GameBarProtocol.KeyPower, ""));

            ApplyModes(state);
            ApplyGpuModes(state);
            ApplyScreenModes(state);
            ApplyControllerModes(state);
            ApplyFpsCaps(state);
            ApplyTelemetry(state);
            ApplyToggles(state);
        }

        private static string PowerLabel(string power)
        {
            if (power == "Battery") return "battery";
            if (power == "Barrel") return "AC";
            if (power == "USBC") return "USB-C";
            return "";
        }

        private void ApplyModes(Dictionary<string, string> state)
        {
            string ids = GameBarProtocol.ReadString(state, GameBarProtocol.KeyModes, "");
            string names = GameBarProtocol.ReadString(state, GameBarProtocol.KeyModeNames, "");
            if (ids.Length == 0) return;

            string signature = ids + "|" + names;
            if (signature != modeSignature)
            {
                modeSignature = signature;
                string[] idList = Split(ids);
                string[] nameList = Split(names);
                string[] labels = new string[idList.Length];
                for (int i = 0; i < idList.Length; i++)
                    labels[i] = i < nameList.Length ? nameList[i] : idList[i];
                Fill(panelModes, GameBarProtocol.SetMode, labels, idList);
            }

            currentMode = GameBarProtocol.ReadString(state, GameBarProtocol.KeyMode, currentMode);
            Restyle(panelModes, currentMode, GameBarProtocol.SetMode);
        }

        private void ApplyGpuModes(Dictionary<string, string> state)
        {
            if (gpuSignature == null)
            {
                gpuSignature = "gpu";
                Fill(panelGpuModes, GameBarProtocol.SetGpuMode,
                    new[] { "Eco", "Standard", "Ultimate" }, new[] { "0", "1", "2" });
            }

            currentGpuMode = GameBarProtocol.ReadString(state, GameBarProtocol.KeyGpuMode, currentGpuMode);
            Restyle(panelGpuModes, currentGpuMode, GameBarProtocol.SetGpuMode);
        }

        private void ApplyScreenModes(Dictionary<string, string> state)
        {
            bool auto = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyScreenAuto, false);
            int min = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyScreenMin, 0);
            int max = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyScreenMax, 0);

            string[] labels =
            {
                "Auto",
                max > 0 ? max + " Hz" : "Max",
                min > 0 ? min + " Hz" : "Min",
            };

            string signature = "screen|" + labels[1] + "|" + labels[2];
            if (signature != screenSignature)
            {
                screenSignature = signature;
                Fill(panelScreenModes, GameBarProtocol.SetScreen, labels, new[] { "0", "1", "2" });
            }

            // Only "auto" is authoritative from the server; the manual rate is not readable
            // back, so the widget remembers what it last selected.
            if (auto) currentScreenMode = "0";
            else if (currentScreenMode == "0") currentScreenMode = "";

            Restyle(panelScreenModes, currentScreenMode, GameBarProtocol.SetScreen);
        }

        private void ApplyControllerModes(Dictionary<string, string> state)
        {
            if (!ally) return;

            string options = GameBarProtocol.ReadString(state, GameBarProtocol.KeyControllerModes, "");
            if (options.Length == 0) return;

            if (options != controllerSignature)
            {
                controllerSignature = options;
                string[] values = Split(options);
                string[] labels = new string[values.Length];
                for (int i = 0; i < values.Length; i++) labels[i] = Capitalize(values[i]);
                Fill(panelControllerModes, GameBarProtocol.SetControllerMode, labels, values);
            }

            currentControllerMode = GameBarProtocol.ReadString(state, GameBarProtocol.KeyControllerMode, currentControllerMode);
            Restyle(panelControllerModes, currentControllerMode, GameBarProtocol.SetControllerMode);
        }

        private void ApplyFpsCaps(Dictionary<string, string> state)
        {
            if (!ally) return;

            string options = GameBarProtocol.ReadString(state, GameBarProtocol.KeyFpsLimitOptions, "");
            if (options.Length == 0) return;

            if (options != fpsSignature)
            {
                fpsSignature = options;
                List<string> labels = new List<string>();
                List<string> values = new List<string>();
                foreach (string value in Split(options))
                {
                    int cap;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cap)) continue;
                    labels.Add(cap <= 0 ? "Off" : cap.ToString(CultureInfo.InvariantCulture));
                    values.Add(value);
                }
                Fill(panelFpsCaps, GameBarProtocol.SetFpsLimit, labels.ToArray(), values.ToArray());
            }

            currentFpsCap = GameBarProtocol.ReadString(state, GameBarProtocol.KeyFpsLimit, currentFpsCap);
            Restyle(panelFpsCaps, currentFpsCap, GameBarProtocol.SetFpsLimit);
        }

        private void ApplyTelemetry(Dictionary<string, string> state)
        {
            int fps = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyFps, -1);
            txtFps.Text = fps > 0 ? fps.ToString(CultureInfo.InvariantCulture) : "--";

            int cpuLoad = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyCpuLoad, -1);
            int gpuLoad = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyGpuLoad, -1);
            int memLoad = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyMemLoad, -1);

            SetGauge(gaugeCpu, cpuLoad);
            SetGauge(gaugeGpu, gpuLoad);
            SetGauge(gaugeMem, memLoad);

            int cpuTemp = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyCpuTemp, -1);
            int gpuTemp = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyGpuTemp, -1);
            txtCpuTemp.Text = cpuTemp > 0 ? cpuTemp + "°C" : "";
            txtGpuTemp.Text = gpuTemp > 0 ? gpuTemp + "°C" : "";
            txtMemLoad.Text = memLoad >= 0 ? memLoad + "%" : "";

            string focused = GameBarProtocol.ReadString(state, GameBarProtocol.KeyFocusedApp, "");
            txtFocusedApp.Text = focused;

            int tdp = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyTdp, -1);
            txtTdp.Text = tdp > 0 ? tdp + "W" : "--";
        }

        private static void SetGauge(ProgressBar bar, int value)
        {
            if (value < 0) return;
            if (Math.Abs(bar.Value - value) < 0.5) return;
            bar.Value = value;
        }

        private void ApplyToggles(Dictionary<string, string> state)
        {
            currentFnLock = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyFnLock, currentFnLock);
            currentOverlay = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyOverlay, currentOverlay);
            currentBatteryFull = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyBatteryFull, currentBatteryFull);
            currentAutoTdp = GameBarProtocol.ReadBool(state, GameBarProtocol.KeyAutoTdp, currentAutoTdp);

            Paint(buttonFnLock, currentFnLock, false);
            Paint(buttonOverlay, currentOverlay, false);
            Paint(buttonBatteryFull, currentBatteryFull, false);
            Paint(buttonAutoTdp, currentAutoTdp, false);

            currentVibration = GameBarProtocol.ReadString(state, GameBarProtocol.KeyVibration, "0");
            Paint(buttonVibrationOff, currentVibration == "0", false);
            Paint(buttonVibrationOn, currentVibration == "1", false);
        }

        // ── Segmented button plumbing ───────────────────────────────────────

        private static string[] Split(string csv)
        {
            return csv.Length == 0 ? new string[0] : csv.Split(',');
        }

        private static string Capitalize(string value)
        {
            if (value.Length == 0) return value;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private void Fill(Panel panel, string key, string[] labels, string[] values)
        {
            panel.Children.Clear();

            for (int i = 0; i < values.Length; i++)
            {
                // Declared inside the loop so each closure captures its own value.
                string capturedKey = key;
                string capturedValue = values[i];

                var button = new Button
                {
                    Content = labels[i],
                    Tag = values[i],
                    Style = (Style)Resources["Segment"],
                };

                button.Click += async (s, e) => await OnSegmentClick(capturedKey, capturedValue);
                panel.Children.Add(button);
            }
        }

        private async Task OnSegmentClick(string key, string value)
        {
            if (lifetime == null || lifetime.IsCancellationRequested) return;

            // Optimistic: show the choice straight away, then let the server confirm it.
            pendingKey = key + "=" + value;
            RestyleAll();

            await SendAsync(key, value, CardFor(key));
        }

        private async Task SendAsync(string key, string value, Border card)
        {
            bool ok = await client.TrySetAsync(key, value, lifetime.Token);

            if (lifetime == null || lifetime.IsCancellationRequested) return;

            pendingKey = null;

            try
            {
                await RefreshAsync(lifetime.Token);
            }
            catch (Exception)
            {
                SetOffline();
            }

            if (ok) Pulse(card);
        }

        /// <summary>Which card a control lives on, so the right one can pulse.</summary>
        private Border CardFor(string key)
        {
            if (key == GameBarProtocol.SetMode) return cardPerformance;
            if (key == GameBarProtocol.SetGpuMode) return cardPerformance;
            if (key == GameBarProtocol.SetScreen) return cardPerformance;
            if (key == GameBarProtocol.SetControllerMode) return cardGamepad;
            if (key == GameBarProtocol.SetFpsLimit) return cardGamepad;
            if (key == GameBarProtocol.SetAutoTdp) return cardGamepad;
            if (key == GameBarProtocol.SetVibration) return cardGamepad;
            return cardToggles;
        }

        private void Restyle(Panel panel, string current, string key)
        {
            foreach (var child in panel.Children)
            {
                var button = child as Button;
                if (button == null) continue;

                string value = (string)button.Tag;
                bool selected = value == current;
                bool pending = key != null && pendingKey == key + "=" + value;

                Paint(button, selected, pending);
            }
        }

        private void Paint(Button button, bool selected, bool pending)
        {
            if (selected)
            {
                button.Background = BrushAccentSoft;
                button.Foreground = BrushAccent;
                button.BorderBrush = BrushAccent;
            }
            else
            {
                button.Background = BrushIdleButton;
                button.Foreground = BrushIdleText;
                button.BorderBrush = BrushCardStroke;
            }

            button.BorderThickness = new Thickness(1);
            button.Opacity = pending ? 0.65 : 1.0;
        }

        private void RestyleAll()
        {
            Restyle(panelModes, currentMode, GameBarProtocol.SetMode);
            Restyle(panelGpuModes, currentGpuMode, GameBarProtocol.SetGpuMode);
            Restyle(panelScreenModes, currentScreenMode, GameBarProtocol.SetScreen);
            Restyle(panelControllerModes, currentControllerMode, GameBarProtocol.SetControllerMode);
            Restyle(panelFpsCaps, currentFpsCap, GameBarProtocol.SetFpsLimit);
        }

        // ── Individual controls ─────────────────────────────────────────────

        private async void VibrationClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || lifetime == null || lifetime.IsCancellationRequested) return;

            string value = (string)button.Tag;
            pendingKey = GameBarProtocol.SetVibration + "=" + value;
            RestyleAll();

            await SendAsync(GameBarProtocol.SetVibration, value, cardGamepad);
        }

        /// <summary>
        /// A control that is either on or off. The new state is shown straight away and
        /// then confirmed by the server, exactly like the segmented choices.
        /// </summary>
        private async Task ToggleAsync(string key, bool value, Button button, Border card)
        {
            if (lifetime == null || lifetime.IsCancellationRequested) return;

            pendingKey = key + "=" + (value ? "1" : "0");
            Paint(button, value, true);
            RestyleAll();

            await SendAsync(key, value ? "1" : "0", card);
        }

        private async void FnLockClick(object sender, RoutedEventArgs e)
        {
            await ToggleAsync(GameBarProtocol.SetFnLock, !currentFnLock, buttonFnLock, cardToggles);
        }

        private async void OverlayClick(object sender, RoutedEventArgs e)
        {
            await ToggleAsync(GameBarProtocol.SetOverlay, !currentOverlay, buttonOverlay, cardToggles);
        }

        private async void BatteryFullClick(object sender, RoutedEventArgs e)
        {
            await ToggleAsync(GameBarProtocol.SetBatteryFull, !currentBatteryFull, buttonBatteryFull, cardToggles);
        }

        private async void AutoTdpClick(object sender, RoutedEventArgs e)
        {
            await ToggleAsync(GameBarProtocol.SetAutoTdp, !currentAutoTdp, buttonAutoTdp, cardGamepad);
        }

        // ── Feedback ───────────────────────────────────────────────────────

        /// <summary>
        /// A short pulse on the card a change landed in. It is the only feedback a glance
        /// gives, so it has to be quick but visible.
        /// </summary>
        private static void Pulse(Border card)
        {
            if (card == null) return;

            try
            {
                var scale = new CompositeTransform();
                card.RenderTransform = scale;

                var storyboard = new Storyboard();

                var scaleX = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.015,
                    Duration = new Duration(TimeSpan.FromMilliseconds(110)),
                    AutoReverse = true,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleY = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.015,
                    Duration = new Duration(TimeSpan.FromMilliseconds(110)),
                    AutoReverse = true,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var fade = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.72,
                    Duration = new Duration(TimeSpan.FromMilliseconds(110)),
                    AutoReverse = true
                };

                Storyboard.SetTarget(scaleX, scale);
                Storyboard.SetTargetProperty(scaleX, "ScaleX");
                Storyboard.SetTarget(scaleY, scale);
                Storyboard.SetTargetProperty(scaleY, "ScaleY");
                Storyboard.SetTarget(fade, card);
                Storyboard.SetTargetProperty(fade, "Opacity");

                storyboard.Children.Add(scaleX);
                storyboard.Children.Add(scaleY);
                storyboard.Children.Add(fade);
                storyboard.Begin();
            }
            catch (Exception)
            {
                // Feedback is a nicety; never let it break the widget.
            }
        }

        // ── Game Bar state ─────────────────────────────────────────────────

        private void OnCompactModeChanged()
        {
            // Nothing to collapse: the whole surface scrolls. Keeping this handler means a
            // future compact layout has a single place to hook in.
        }

        private void ApplyTheme()
        {
            if (widget != null) Root.RequestedTheme = WidgetHost.ThemeOf(widget);

            ApplyPalette();
        }
    }
}