using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace GHelper.GameBar
{
    /// <summary>
    /// Diagnostics for the widget, written where a developer can read them.
    ///
    /// A widget runs sandboxed with no console and no visible window when it is hosted by
    /// Game Bar, so a failure is otherwise completely invisible. The log lives in the app's
    /// own LocalState folder:
    ///
    ///     %LOCALAPPDATA%\Packages\&lt;PackageFamilyName&gt;\LocalState\GHelperWidget.log
    /// </summary>
    internal static class WidgetLog
    {
        private static readonly object Lock = new object();
        private static string _path;

        private static string Path
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        _path = System.IO.Path.Combine(
                            ApplicationData.Current.LocalFolder.Path, "GHelperWidget.log");
                    }
                    catch (Exception)
                    {
                        _path = "GHelperWidget.log";
                    }
                }

                return _path;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    using (StreamWriter writer = new StreamWriter(
                        Path, true, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(DateTime.Now.ToString("u") + " " + message);
                    }
                }
            }
            catch (Exception)
            {
                // Never let logging break the app.
            }
        }
    }
}