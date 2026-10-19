using Microsoft.Gaming.XboxGameBar;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace GHelper.GameBar
{
    /// <summary>
    /// Companion page opened from the gear icon on the main widget's title bar.
    ///
    /// Every setting the widget can change lives on the main widget, so this is purely
    /// informational: connection state, what G-Helper currently has applied, and a manual
    /// reconnect. Keeping it read-only avoids two widgets fighting over the same toggle.
    /// </summary>
    public sealed partial class GHelperSettingsWidget : Page
    {
        private const int PollIntervalMs = 3000;

        /// <summary>Shared wording with the main widget so there is one string to translate.</summary>
        private const string StatusConnected = "connected";
        private const string StatusOffline = "G-Helper not running";

        // Shared with the main widget: both pages live in this process and the desktop
        // server serves one client at a time.
        private readonly GHelperClient client = GHelperClient.Shared;

        // The Game Bar widget object, held as object so that the SDK's type is not resolved when
        // this page's type is loaded. WidgetHost does the typed work.
        private object widget;
        private IDisposable widgetSubscription;
        private DispatcherTimer timer;
        private System.Threading.CancellationTokenSource lifetime;
        private bool busy;

        /// <summary>True while the bridge answered; drives the status dot on theme change.</summary>
        private bool connected;

        public GHelperSettingsWidget()
        {
            InitializeComponent();
            SetOffline();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            widget = e.Parameter;
            if (widget != null)
            {
                widgetSubscription = WidgetHost.Subscribe(widget, ApplyTheme, () => { });
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

            // The client is deliberately NOT disposed here. It is the process-wide shared
            // connection, and the main widget may still be using it.
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
            Apply(state);
        }

        private void Apply(Dictionary<string, string> state)
        {
            SetOnline();

            txtModel.Text = GameBarProtocol.ReadString(state, GameBarProtocol.KeyModel, "G-Helper");
            txtMode.Text = "Mode: " + GameBarProtocol.ReadString(state, GameBarProtocol.KeyModeName, "-");

            int gpuMode = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyGpuMode, -1);
            string gpu = gpuMode == 0 ? "Eco" : gpuMode == 1 ? "Standard" : gpuMode == 2 ? "Ultimate" : "-";
            txtGpuMode.Text = "GPU: " + gpu;

            int version = GameBarProtocol.ReadInt(state, GameBarProtocol.KeyVersion, 0);
            txtBridge.Text = "Bridge protocol v" + version.ToString(CultureInfo.InvariantCulture);
        }

        private void SetOnline()
        {
            connected = true;
            dotStatus.Fill = StatusOnlineBrush();
            txtStatus.Text = StatusConnected;
        }

        private void SetOffline()
        {
            connected = false;
            dotStatus.Fill = StatusOfflineBrush();
            txtStatus.Text = StatusOffline;
            txtBridge.Text = "";
        }

        // UWP has no FindResource that reaches ThemeDictionaries entries, so the status dot
        // colour is built from the effective theme. The static chrome in the XAML uses
        // {ThemeResource} and resolves itself.
        private bool IsDark
        {
            get { return Root.RequestedTheme != Windows.UI.Xaml.ElementTheme.Light; }
        }

        // UWP's Color.FromArgb is (a, r, g, b) all byte - there is no int overload.
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush { Color = Color.FromArgb(a, r, g, b) };
        }

        private SolidColorBrush StatusOnlineBrush()
        {
            return IsDark ? Brush(0xFF, 0x3E, 0xCF, 0x3E) : Brush(0xFF, 0x1E, 0x8B, 0x2E);
        }

        private SolidColorBrush StatusOfflineBrush()
        {
            return IsDark ? Brush(0xFF, 0xFF, 0x6B, 0x6B) : Brush(0xFF, 0xC4, 0x2B, 0x1C);
        }

        private async void RetryClick(object sender, RoutedEventArgs e)
        {
            if (lifetime == null || lifetime.IsCancellationRequested) return;

            client.Disconnect();

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

        private void WidgetRequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            ApplyTheme();

            // The status dot colour is built in code, so redraw it on a theme change.
            if (connected) SetOnline();
        }

        private void ApplyTheme()
        {
            if (widget != null) Root.RequestedTheme = WidgetHost.ThemeOf(widget);
        }
    }
}