using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GHelper.GameBar
{
    /// <summary>
    /// Named pipe client for the bridge served by the G-Helper desktop process
    /// (see app/Helpers/GameBarServer.cs).
    ///
    /// The pipe is opened with the Win32 CreateFile API rather than
    /// System.IO.Pipes.NamedPipeClientStream, because the widget runs in an AppContainer
    /// and that type fails outright there ("not supported in app container",
    /// dotnet/runtime#25764). CreateFile is permitted as long as the pipe's security
    /// descriptor grants access, which the server installs.
    ///
    /// Loopback TCP is deliberately not used: it works while Visual Studio deploys the app
    /// (which auto-grants a loopback exemption) and then fails once the package is
    /// sideloaded. See app/Shared/GameBarProtocol.cs for the full reasoning.
    ///
    /// One connection is held open for the lifetime of the page and reused for every
    /// request. Anything that fails tears the handle down so the next call reconnects,
    /// which is what makes "G-Helper is not running" a recoverable state rather than a
    /// permanently broken widget.
    /// </summary>
    public sealed class GHelperClient : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const int ERROR_PIPE_BUSY = 231;

        /// <summary>How long to keep retrying before giving up on a connect.</summary>
        private const int ConnectTimeoutMs = 1500;

        /// <summary>The desktop side never sends a reply this long.</summary>
        private const int MaxReplyBytes = 8192;

        /// <summary>Serialises requests; each request is a full round trip.</summary>
        private readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WaitNamedPipeW(string pipeName, int timeOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint count, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, uint count, out uint written, IntPtr overlapped);

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private SafeFileHandle pipe;

        private static readonly object SharedLock = new object();
        private static GHelperClient _shared;

        /// <summary>
        /// One connection shared by every widget page in this process.
        ///
        /// The desktop server can only serve a single client at a time (see GameBarServer:
        /// .NET sets FILE_FLAG_FIRST_PIPE_INSTANCE on every CreateNamedPipe call, so extra
        /// pipe instances cannot be created that way). The main widget and the settings
        /// widget live in the same process, so sharing one client means only one connection
        /// is ever needed - otherwise opening the settings page while the main widget is
        /// showing would leave it stuck reporting "offline".
        ///
        /// Requests are serialised by requestLock, so the pages can use it concurrently.
        /// </summary>
        public static GHelperClient Shared
        {
            get
            {
                lock (SharedLock)
                {
                    if (_shared == null) _shared = new GHelperClient();
                    return _shared;
                }
            }
        }

        private static string PipePath
        {
            get { return @"\\.\pipe\" + GameBarProtocol.PipeName; }
        }

        public bool IsConnected
        {
            get { return pipe != null && !pipe.IsInvalid && !pipe.IsClosed; }
        }

        public async Task ConnectAsync()
        {
            if (IsConnected) return;

            Disconnect();

            WidgetLog.Write("connecting to \\\\.\\pipe\\" + GameBarProtocol.PipeName);

            // WaitNamedPipe returns false while every instance is busy, which is the normal
            // case when another widget already holds the connection. Retry until the
            // deadline so a fresh connect after a drop is not permanently poisoned.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ConnectTimeoutMs);

            while (true)
            {
                WaitNamedPipeW(PipePath, 50);

                SafeFileHandle handle = CreateFileW(
                    PipePath,
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (!handle.IsInvalid)
                {
                    pipe = handle;
                    return;
                }

                int error = Marshal.GetLastWin32Error();

                if (error != ERROR_PIPE_BUSY && DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("G-Helper bridge unavailable (error " + error + ")");

                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("G-Helper bridge busy");

                if (error != 0)
                {
                    WidgetLog.Write("CreateFile on the bridge pipe failed: Win32 " + error +
                                    " (ERROR_FILE_NOT_FOUND=2, ERROR_ACCESS_DENIED=5, ERROR_PIPE_BUSY=231)");
                }

                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        public void Disconnect()
        {
            if (pipe != null)
            {
                try { pipe.Dispose(); } catch { }
                pipe = null;
            }
        }

        /// <summary>
        /// Sends a request and returns the reply's payload, or throws if the bridge is not
        /// reachable. Callers should treat any exception as "G-Helper is offline".
        /// </summary>
        public async Task<string> SendAsync(string request, CancellationToken cancellationToken)
        {
            await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsConnected) await ConnectAsync().ConfigureAwait(false);

                WriteLine(request);
                string line = ReadLine();

                string status;
                string payload;
                if (!GameBarProtocol.TryParseReply(line, out status, out payload))
                {
                    Disconnect();
                    throw new InvalidOperationException("Unexpected reply from G-Helper");
                }

                if (string.Equals(status, GameBarProtocol.ReplyError, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(payload);

                return payload;
            }
            catch
            {
                // Any failure invalidates the connection: a half-read reply would
                // desynchronise the next request, so always drop it rather than reuse it.
                Disconnect();
                throw;
            }
            finally
            {
                requestLock.Release();
            }
        }

        public async Task<Dictionary<string, string>> GetStateAsync(CancellationToken cancellationToken)
        {
            string payload = await SendAsync(GameBarProtocol.VerbState, cancellationToken).ConfigureAwait(false);
            return GameBarProtocol.ParseState(payload);
        }

        /// <summary>
        /// Issues a SET command. Returns false when G-Helper is offline or rejects the
        /// command, so the UI can quietly retry rather than throw.
        /// </summary>
        public async Task<bool> TrySetAsync(string key, string value, CancellationToken cancellationToken)
        {
            try
            {
                await SendAsync(GameBarProtocol.Request(GameBarProtocol.VerbSet, key, value), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WriteLine(string line)
        {
            byte[] payload = Utf8.GetBytes(line + "\n");
            uint written;

            if (!WriteFile(pipe, payload, (uint)payload.Length, out written, IntPtr.Zero) || written != payload.Length)
                throw new InvalidOperationException("Failed to write to the G-Helper bridge");
        }

        private string ReadLine()
        {
            byte[] buffer = new byte[512];
            List<byte> line = new List<byte>(128);

            while (true)
            {
                uint read;
                if (!ReadFile(pipe, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                    throw new InvalidOperationException("G-Helper closed the bridge");

                // Byte-mode pipe: a read can return fewer bytes than requested, so keep
                // going until the terminating newline shows up.
                for (uint i = 0; i < read; i++)
                {
                    byte next = buffer[i];
                    if (next == (byte)'\n') return Utf8.GetString(line.ToArray());

                    line.Add(next);

                    // The desktop side never sends anything this long; treat it as a
                    // desynced stream rather than growing without bound.
                    if (line.Count > MaxReplyBytes) throw new InvalidOperationException("Reply too long");
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
            requestLock.Dispose();
        }
    }
}