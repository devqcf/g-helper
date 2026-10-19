using Microsoft.Gaming.XboxGameBar;
using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace GHelper.GameBar
{
    /// <summary>
    /// The only place that references the Game Bar SDK's types.
    ///
    /// The CLR resolves field types when it loads a type, so an <c>XboxGameBarWidget</c>
    /// field anywhere in the app's startup path means the SDK's native implementation has
    /// to load before the constructor runs - and before any exception handler is in place.
    /// When that implementation cannot be loaded the app dies instantly with a CLR
    /// exception and no diagnostics.
    ///
    /// Keeping every strong reference in this class means the type is only loaded when a
    /// widget is actually being created, so the app can still start (and be debugged)
    /// outside Game Bar.
    /// </summary>
    internal static class WidgetHost
    {
        /// <summary>
        /// Creates a widget for <paramref name="args"/> and navigates <paramref name="frame"/>
        /// to the matching page. Returns the widget object, which the caller keeps alive for
        /// the lifetime of the window.
        /// </summary>
        public static object Create(
            XboxGameBarWidgetActivatedEventArgs args,
            CoreWindow coreWindow,
            Frame frame,
            bool isSettings)
        {
            XboxGameBarWidget widget = new XboxGameBarWidget(args, coreWindow, frame);

            if (isSettings)
            {
                frame.Navigate(typeof(GHelperSettingsWidget), widget);
            }
            else
            {
                // Tell Game Bar what this widget can do. These are runtime properties on
                // XboxGameBarWidget, separate from the manifest declarations, and they are
                // what makes Game Bar draw the pin and gear buttons on the title bar.
                widget.HomeMenuVisibleSupported = true;
                widget.PinningSupported = true;
                widget.SettingsSupported = true;

                frame.Navigate(typeof(GHelperWidget), widget);
            }

            return widget;
        }

        /// <summary>
        /// Subscribes to the widget's events without leaking the SDK type to the caller.
        /// Dispose the returned object to unsubscribe.
        /// </summary>
        public static IDisposable Subscribe(object widget, Action themeChanged, Action compactChanged)
        {
            XboxGameBarWidget typed = (XboxGameBarWidget)widget;

            void OnTheme(object sender, object args) => themeChanged();
            void OnCompact(object sender, object args) => compactChanged();

            typed.RequestedThemeChanged += OnTheme;
            typed.CompactModeEnabledChanged += OnCompact;

            return new Unsubscriber(() =>
            {
                typed.RequestedThemeChanged -= OnTheme;
                typed.CompactModeEnabledChanged -= OnCompact;
            });
        }

        private sealed class Unsubscriber : IDisposable
        {
            private Action _unsubscribe;

            public Unsubscriber(Action unsubscribe) { _unsubscribe = unsubscribe; }

            public void Dispose()
            {
                Action action = _unsubscribe;
                _unsubscribe = null;
                if (action != null) action();
            }
        }

        /// <summary>The theme Game Bar is asking the widget to use.</summary>
        public static ElementTheme ThemeOf(object widget)
        {
            return ((XboxGameBarWidget)widget).RequestedTheme;
        }
    }
}