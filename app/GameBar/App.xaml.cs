using Microsoft.Gaming.XboxGameBar;
using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace GHelper.GameBar
{
    /// <summary>
    /// Hosts the G-Helper Game Bar widgets.
    ///
    /// Game Bar does not launch this app like a normal foreground app - it activates it with
    /// a Protocol activation whose scheme is "ms-gamebarwidget". Every widget gets its own
    /// CoreWindow, and the AppExtensionId in the activation tells us which page to show.
    /// See https://learn.microsoft.com/en-us/xbox/game-bar/guide/app-activation
    /// </summary>
    sealed partial class App : Application
    {
        /// <summary>App extension ids, kept in sync with Package.appxmanifest.</summary>
        private const string WidgetExtensionId = "GHelperWidget";
        private const string SettingsExtensionId = "GHelperSettings";

        /// <summary>
        /// The widget's size in Game Bar, in device independent pixels. Kept in step with
        /// the &lt;Window&gt;&lt;Size&gt; block in Package.appxmanifest.
        /// </summary>
        private const double WidgetWidth = 560;
        private const double WidgetHeight = 440;

        // XboxGameBarWidget is held as object on purpose. The CLR resolves field types when
        // it loads a type, so a strongly typed field here would force the Game Bar SDK's
        // native implementation to load before the App constructor - and before any
        // exception handler is in place. WidgetHost keeps the strong typing where it is
        // only needed.
        private object widget;
        private object settingsWidget;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// A widget that crashes inside Game Bar is invisible - Game Bar just shows an empty
        /// frame. Writing the exception somewhere readable is the only way to diagnose it.
        /// </summary>
        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                string path = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "GameBarWidget-errors.log");

                File.AppendAllText(path,
                    DateTime.Now.ToString("u") + Environment.NewLine +
                    e.Exception.ToString() + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // Never let the handler itself throw.
            }
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = AsWidgetArgs(args);
            if (widgetArgs is null) return;

            // On a launch activation Game Bar has just created a new CoreWindow for us, so we
            // must build a fresh XboxGameBarWidget and navigate to the matching page. On a
            // repeat activation the widget already exists and must be kept alive - recreating
            // it would tear down the connection Game Bar thinks it still holds.
            if (!widgetArgs.IsLaunchActivation)
            {
                HandleRepeatActivation(widgetArgs);
                return;
            }

            bool isSettingsWidget = widgetArgs.AppExtensionId == SettingsExtensionId;

            // Validate before building anything. Returning after assigning Window.Content
            // would leave an un-activated blank frame on screen.
            if (!isSettingsWidget && widgetArgs.AppExtensionId != WidgetExtensionId)
            {
                // This assembly cannot use G-Helper's Logger (that lives in GHelper.dll,
                // which the widget does not reference), so trace locally instead.
                System.Diagnostics.Debug.WriteLine("Unknown app extension id: " + widgetArgs.AppExtensionId);
                Window.Current.Close();
                return;
            }

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;

            // Honour the system language's layout direction.
            string layout = Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView()
                .QualifierValues["LayoutDirection"];
            rootFrame.FlowDirection = layout == "RTL" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            Window.Current.Content = rootFrame;

            if (isSettingsWidget)
            {
                settingsWidget = WidgetHost.Create(widgetArgs, Window.Current.CoreWindow, rootFrame, true);
                Window.Current.Closed += SettingsWindowClosed;
            }
            else
            {
                widget = WidgetHost.Create(widgetArgs, Window.Current.CoreWindow, rootFrame, false);
                Window.Current.Closed += WidgetWindowClosed;
            }

            Window.Current.Activate();
        }

        private static XboxGameBarWidgetActivatedEventArgs AsWidgetArgs(IActivatedEventArgs args)
        {
            if (args.Kind != ActivationKind.Protocol) return null;

            var protocolArgs = args as IProtocolActivatedEventArgs;
            if (protocolArgs is null) return null;

            if (!string.Equals(protocolArgs.Uri.Scheme, "ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
                return null;

            return args as XboxGameBarWidgetActivatedEventArgs;
        }

        /// <summary>
        /// Repeat activations let one widget command another. This widget does not define
        /// any widget-to-widget commands, so there is nothing to do beyond staying alive.
        /// </summary>
        private static void HandleRepeatActivation(XboxGameBarWidgetActivatedEventArgs widgetArgs)
        {
        }

        private void WidgetWindowClosed(object sender, CoreWindowEventArgs args)
        {
            widget = null;
            Window.Current.Closed -= WidgetWindowClosed;
        }

        private void SettingsWindowClosed(object sender, CoreWindowEventArgs args)
        {
            settingsWidget = null;
            Window.Current.Closed -= SettingsWindowClosed;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // A plain foreground launch means Game Bar is not hosting us. That is the
            // supported way to iterate on a widget without Game Bar in the loop
            // ("Outside Game Bar" in the Game Bar SDK testing guide), so show the real
            // widget page rather than a placeholder - it is the same UI, the same XAML and
            // the same bridge client, and it exercises the AppContainer -> named pipe path
            // that matters most.
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame is null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (args.PrelaunchActivated) return;

            if (rootFrame.Content is null) rootFrame.Navigate(typeof(GHelperWidget));

            SizeToWidgetSize();

            Window.Current.Activate();
        }

        /// <summary>
        /// Outside Game Bar there is no host to size the window, so size it the way Game
        /// Bar would. The standalone preview then shows exactly what the widget looks like
        /// in the Game Bar, which is what makes it worth having.
        /// </summary>
        private static void SizeToWidgetSize()
        {
            try
            {
                var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();

                // The extra height covers the window's own chrome; the content is what
                // the widget actually controls.
                view.TryResizeView(new Windows.Foundation.Size(WidgetWidth, WidgetHeight + 40));
            }
            catch (Exception)
            {
                // Sizing is a convenience. If the platform refuses, the preview still works.
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // Only fires once the last widget window has closed. Release the widget objects
            // and let the bridge connection go with the page.
            var deferral = e.SuspendingOperation.GetDeferral();

            widget = null;
            settingsWidget = null;

            deferral.Complete();
        }
    }
}