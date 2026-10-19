// Hand-written stubs for the WinRT / Xbox Game Bar types the widget uses.
//
// These let the widget's C# be compiled and type-checked on a machine that has only the
// .NET SDK, with no UWP workload or Windows SDK installed. They are NEVER part of the
// shipped package - at build time the real types come from Windows SDK metadata and the
// Microsoft.Gaming.XboxGameBar winmd. See scripts/Check-WidgetSources.ps1.
//
// Only the members the widget actually touches are declared, and none of them have
// bodies worth reading. If the widget starts using something new, expect to add it here.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Windows.Foundation
{
    public interface IAsyncOperation { }
    public class Deferral { public void Complete() { } }

    public struct Size
    {
        public double Width { get; }
        public double Height { get; }

        public Size(double width, double height)
        {
            Width = width;
            Height = height;
        }
    }
}

namespace Windows.UI.ViewManagement
{
    public class ApplicationView
    {
        public static ApplicationView GetForCurrentView() => new ApplicationView();

        public bool TryResizeView(Windows.Foundation.Size size) => true;
    }
}

namespace Windows.Foundation.Collections
{
    public class ValueSet : Dictionary<string, object> { }
}

namespace Windows.ApplicationModel.Activation
{
    public enum ActivationKind { Launch, Protocol, File, ShareTarget, Unknown }
    public enum ApplicationExecutionState { NotRunning, Running, Terminated, ClosedByUser, Suspended }

    public interface IActivatedEventArgs { ActivationKind Kind { get; } }

    public interface IProtocolActivatedEventArgs : IActivatedEventArgs { Uri Uri { get; } }

    public class LaunchActivatedEventArgs : IActivatedEventArgs
    {
        public ActivationKind Kind => ActivationKind.Launch;
        public bool PrelaunchActivated { get; set; }
        public ApplicationExecutionState PreviousExecutionState { get; set; }
        public string Arguments { get; set; }
    }
}

namespace Windows.ApplicationModel
{
    public class SuspendingOperation
    {
        public Windows.Foundation.Deferral GetDeferral() => new Windows.Foundation.Deferral();
    }

    public class SuspendingEventArgs
    {
        public SuspendingOperation SuspendingOperation { get; } = new SuspendingOperation();
    }
}

namespace Windows.ApplicationModel.Resources.Core
{
    public class ResourceContext
    {
        public static ResourceContext GetForCurrentView() => new ResourceContext();
        public IReadOnlyDictionary<string, string> QualifierValues => new Dictionary<string, string>();
    }
}

namespace Windows.System
{
    public static class Launcher
    {
        public static Task<bool> LaunchUriAsync(Uri uri) => Task.FromResult(true);
    }
}

namespace Windows.UI.Core
{
    public class CoreWindowEventArgs { }

    public delegate void TypedEventHandler<TSender, TArgs>(TSender sender, TArgs args);

    public class CoreWindow { }
}

namespace Windows.UI.Xaml
{
    public enum ElementTheme { Default, Light, Dark }
    public enum FlowDirection { LeftToRight, RightToLeft }
    public enum Visibility { Visible, Collapsed, Hidden }
    public enum VerticalAlignment { Stretch, Top, Center, Bottom }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis, WordEllipsis }

    public struct Thickness
    {
        public Thickness(double uniformLength) { }
        public Thickness(double l, double t, double r, double b) { }
    }
    public struct CornerRadius { public CornerRadius(double r) { } }
    public struct Duration { public Duration(TimeSpan t) { } }

    public class RoutedEventArgs : EventArgs { }

    public class UnhandledExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public bool Handled { get; set; }
    }
    public class DependencyObject { }

    public class Style { }

    public class ResourceDictionary : Dictionary<object, object> { }

    public class UIElement : DependencyObject
    {
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; }
        public bool IsEnabled { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public double FontSize { get; set; }
        public Media.Transform RenderTransform { get; set; }
    }

    public class FrameworkElement : UIElement
    {
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public FlowDirection FlowDirection { get; set; }
        public Media.Brush Background { get; set; }
        public ElementTheme RequestedTheme { get; set; }
        public object Content { get; set; }
        public Style Style { get; set; }
        public ResourceDictionary Resources { get; } = new ResourceDictionary();
    }

    public class Window
    {
        public static Window Current { get; } = new Window();
        public object Content { get; set; }
        public Windows.UI.Core.CoreWindow CoreWindow { get; } = new Windows.UI.Core.CoreWindow();
        public event Windows.UI.Core.TypedEventHandler<Windows.UI.Core.CoreWindow, Windows.UI.Core.CoreWindowEventArgs> Closed;
        public void Activate() { }
        public void Close() { }
    }

    public class Application
    {
        public event EventHandler<Windows.ApplicationModel.SuspendingEventArgs> Suspending;
        public event EventHandler<UnhandledExceptionEventArgs> UnhandledException;
        public void InitializeComponent() { }
        protected virtual void OnActivated(Windows.ApplicationModel.Activation.IActivatedEventArgs args) { }
        protected virtual void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs args) { }
    }

    public class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler<object> Tick;
        public void Start() { }
        public void Stop() { }
    }
}

namespace Windows.UI
{
    public struct Color
    {
        // UWP has only the all-byte overloads - there is no Color.FromArgb(int, int, int).
        public static Color FromArgb(byte a, byte r, byte g, byte b) => default(Color);
    }
}

namespace Windows.Storage
{
    public class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public LocalFolder LocalFolder { get; } = new LocalFolder();
    }

    public class LocalFolder
    {
        // global:: is needed here: inside Windows.Storage, the name "System" would bind to
        // the sibling namespace Windows.System rather than the global one.
        public string Path { get; set; } = global::System.IO.Path.GetTempPath();
    }
}

namespace Windows.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush : Brush { public Windows.UI.Color Color { get; set; } }
    public class Transform { }

    public class CompositeTransform : Transform
    {
        public double ScaleX { get; set; }
        public double ScaleY { get; set; }
    }
}

namespace Windows.UI.Xaml.Media.Animation
{
    public enum EasingMode { EaseIn, EaseOut, EaseInOut }

    public class EasingFunction { }

    public class QuadraticEase : EasingFunction
    {
        public EasingMode EasingMode { get; set; }
    }

    public class Timeline : Windows.UI.Xaml.DependencyObject { }

    public class DoubleAnimation : Timeline
    {
        public double From { get; set; }
        public double To { get; set; }
        public Windows.UI.Xaml.Duration Duration { get; set; }
        public bool AutoReverse { get; set; }
        public EasingFunction EasingFunction { get; set; }
    }

    public class StoryboardChildren : List<Timeline> { }

    public class Storyboard
    {
        public StoryboardChildren Children { get; } = new StoryboardChildren();

        public static void SetTarget(Windows.UI.Xaml.DependencyObject target, object targetObject) { }
        public static void SetTargetProperty(Windows.UI.Xaml.DependencyObject target, string propertyName) { }

        public void Begin() { }
    }
}

namespace Windows.UI.Xaml.Shapes
{
    public class Shape : FrameworkElement { public Media.Brush Fill { get; set; } }
    public class Ellipse : Shape { }
    public class Rectangle : Shape { }
}

namespace Windows.UI.Xaml.Navigation
{
    public class NavigationEventArgs : EventArgs { public object Parameter { get; set; } }
    public class NavigationFailedEventArgs : EventArgs { public Type SourcePageType { get; set; } }
    public class NavigationHelper { public object NavigateParameter { get; set; } }
}

namespace Windows.UI.Xaml.Controls
{
    public class Control : FrameworkElement
    {
        public Media.Brush Foreground { get; set; }
        public Media.Brush BorderBrush { get; set; }
        public Thickness BorderThickness { get; set; }
    }

    public class UIElementCollection : List<UIElement> { }

    public class Panel : FrameworkElement
    {
        public UIElementCollection Children { get; } = new UIElementCollection();
    }

    public enum Orientation { Horizontal, Vertical }

    public class StackPanel : Panel { public Orientation Orientation { get; set; } }

    public class ColumnDefinition { }
    public class RowDefinition { }
    public class ColumnDefinitionCollection : List<ColumnDefinition> { }
    public class RowDefinitionCollection : List<RowDefinition> { }

    public class Grid : Panel
    {
        public ColumnDefinitionCollection ColumnDefinitions { get; } = new ColumnDefinitionCollection();
        public RowDefinitionCollection RowDefinitions { get; } = new RowDefinitionCollection();

        public static void SetColumn(UIElement element, int column) { }
        public static void SetRow(UIElement element, int row) { }
        public static void SetColumnSpan(UIElement element, int span) { }
        public static void SetRowSpan(UIElement element, int span) { }
    }

    // Deliberately NOT stubbed: UniformGrid. It is a WPF-only control that does not exist
    // in UWP, and stubbing it here once let a WMC0001 XamlCompiler error through. Only add
    // a type here if you have confirmed it exists in the UWP control set.
    public class Border : FrameworkElement { }

    public class ProgressBar : Control
    {
        public double Value { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
    }

    public class TextBlock : FrameworkElement
    {
        public string Text { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public TextTrimming TextTrimming { get; set; }
    }

    public class Button : Control
    {
        public object Content { get; set; }
        public object Tag { get; set; }
        public double MinWidth { get; set; }
        public Media.Brush Background { get; set; }
        public Media.Brush Foreground { get; set; }
        public event EventHandler<RoutedEventArgs> Click;
    }

    public class ToggleSwitch : Control
    {
        public bool IsOn { get; set; }
        public event EventHandler<RoutedEventArgs> Toggled;
    }

    public enum ScrollBarVisibility { Disabled, Auto, Visible, Hidden }
    public enum ScrollMode { Disabled, Enabled, Auto }

    public class ScrollViewer : FrameworkElement
    {
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
        public ScrollMode VerticalScrollMode { get; set; }
        public ScrollMode HorizontalScrollMode { get; set; }
    }

    public class Frame : FrameworkElement
    {
        public event EventHandler<Windows.UI.Xaml.Navigation.NavigationFailedEventArgs> NavigationFailed;
        public bool Navigate(Type sourcePageType) => true;
        public bool Navigate(Type sourcePageType, object parameter) => true;
    }

    public class Page : FrameworkElement
    {
        public void InitializeComponent() { }
        // NOTE: no FindResource. UWP has no way to reach ThemeDictionaries entries by key
        // from code, and stubbing it let three real compile errors through. Build brushes
        // from the effective theme instead.
        public Windows.UI.Xaml.Navigation.NavigationHelper NavigationHelper => null;
        protected virtual void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e) { }
        protected virtual void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e) { }
    }
}

namespace Microsoft.Gaming.XboxGameBar
{
    public class XboxGameBarWidgetActivatedEventArgs
        : Windows.ApplicationModel.Activation.IActivatedEventArgs,
          Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs
    {
        public Windows.ApplicationModel.Activation.ActivationKind Kind
            => Windows.ApplicationModel.Activation.ActivationKind.Protocol;
        public Uri Uri { get; set; }
        public bool IsLaunchActivation { get; set; }
        public string AppExtensionId { get; set; }
    }

    public class XboxGameBarWidget
    {
        public XboxGameBarWidget(
            XboxGameBarWidgetActivatedEventArgs args,
            Windows.UI.Core.CoreWindow coreWindow,
            Windows.UI.Xaml.Controls.Frame frame) { }

        public string AppExtensionId { get; set; }
        public Windows.UI.Xaml.ElementTheme RequestedTheme { get; set; }
        public bool CompactModeEnabled { get; set; }
        public bool SettingsSupported { get; set; }
        public bool PinningSupported { get; set; }
        public bool HomeMenuVisibleSupported { get; set; }
        public bool Visible { get; set; }

        public event Windows.UI.Core.TypedEventHandler<XboxGameBarWidget, object> RequestedThemeChanged;
        public event Windows.UI.Core.TypedEventHandler<XboxGameBarWidget, object> CompactModeEnabledChanged;
        public event Windows.UI.Core.TypedEventHandler<XboxGameBarWidget, object> GameBarDisplayModeChanged;
        public event Windows.UI.Core.TypedEventHandler<XboxGameBarWidget, object> SettingsClicked;

        public void Close() { }
    }
}