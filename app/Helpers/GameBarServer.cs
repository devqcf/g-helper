using GHelper.Ally;
using GHelper.Battery;
using GHelper.Display;
using GHelper.Gpu;
using GHelper.Input;
using GHelper.Mode;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

namespace GHelper.Helpers
{
    /// <summary>
    /// Bridge between the G-Helper desktop process and the packaged Xbox Game Bar widget
    /// (app/GameBar). Lets the widget switch performance and hardware settings.
    ///
    /// The widget is an AppContainer process, so the channel is a named pipe - the transport
    /// Microsoft prescribes for widget-to-desktop communication. Loopback TCP is not an option
    /// (it works under the VS debugger and fails once sideloaded) and
    /// System.IO.Pipes.NamedPipeClientStream is rejected outright in an AppContainer, so the
    /// widget opens the pipe with the Win32 CreateFile API instead. See
    /// <see cref="GHelper.GameBar.GameBarProtocol"/> for the full reasoning.
    ///
    /// This is a control channel, not a telemetry one. It reports the state of the settings
    /// the widget can change - it does not poll sensors. G-Helper already refreshes those on
    /// its own timers (overlay, tray hover), and a second poller from here would double the
    /// ACPI/NVML traffic and race with those.
    /// </summary>
    public static class GameBarServer
    {
        /// <summary>
        /// How long the accept thread waits for a client before re-checking whether the set
        /// of installed widget packages has changed. The check is cheap; the wait is what
        /// makes the pipe available.
        /// </summary>
        private const int PipeRefreshSeconds = 5;

        /// <summary>
        /// Declared capacity of the pipe. The server only ever holds a single instance at a
        /// time (see AcceptLoop), so this is a limit for clients rather than something
        /// pre-allocated.
        /// </summary>
        private const int MaxClients = 4;

        /// <summary>
        /// The package name of the Game Bar widget, as declared in its manifest Identity.
        /// Only the publisher hash in the full package name varies with the signing
        /// certificate, so matching on this prefix finds every installed copy.
        /// </summary>
        private const string WidgetPackageName = "GHelperGameBar";

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;

        private const int SE_FILE_OBJECT = 1;
        private const uint DACL_SECURITY_INFORMATION = 0x4;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(
            IntPtr securityDescriptor, out IntPtr dacl, out bool daclPresent, out bool daclDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            IntPtr handle, int objectType, uint securityInfo,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        private static readonly object Sync = new object();
        private static CancellationTokenSource? _cts;
        private static NamedPipeServerStream? _currentServer;
        private static int _clientCount;

        public static bool IsRunning { get; private set; }

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                CancellationTokenSource cts = new CancellationTokenSource();

                // The instance is created up front so a failure is reported here rather
                // than from the accept thread, and so the pipe is available immediately.
                NamedPipeServerStream server = CreateInstance(FindWidgetPackageSids());

                lock (Sync)
                {
                    _cts = cts;
                    _currentServer = server;
                }

                IsRunning = true;

                Thread thread = new Thread(() => _ = AcceptLoop(cts.Token))
                {
                    IsBackground = true,
                    Name = "G-Helper Game Bar bridge"
                };
                thread.Start();

                Logger.WriteLine($"Game Bar bridge: listening on \\\\.\\pipe\\{GameBar.GameBarProtocol.PipeName}");
            }
            catch (Exception ex)
            {
                // Never let the bridge take the app down - another instance of G-Helper may
                // already hold the pipe. The widget will just show "offline".
                IsRunning = false;
                Logger.WriteLine("Game Bar bridge failed to start: " + ex);
            }
        }

        public static void Stop()
        {
            CancellationTokenSource? cts;

            lock (Sync)
            {
                cts = _cts;
                _cts = null;
            }

            IsRunning = false;

            try { cts?.Cancel(); } catch { }

            // Tear down the live instance synchronously. Relying on the accept thread to
            // notice the cancellation leaves a pipe instance behind for a moment, and a
            // client that reconnects in that window gets ERROR_PIPE_BUSY instead of a
            // clean connect.
            NamedPipeServerStream? live;
            lock (Sync)
            {
                live = _currentServer;
                _currentServer = null;
            }

            try { live?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }

            Logger.WriteLine("Game Bar bridge: stopped");
        }

        /// <summary>
        /// Accepts clients, one at a time, on a dedicated thread.
        ///
        /// Creating several instances of the same name up front does not work: .NET sets
        /// FILE_FLAG_FIRST_PIPE_INSTANCE on every CreateNamedPipe call, so the second one
        /// fails with ERROR_ACCESS_DENIED regardless of the security descriptor. Verified on
        /// .NET 10 - it is not an ACL problem. So instances are created one at a time, as
        /// connections arrive.
        ///
        /// Serial handling is sufficient. A round trip is a few microseconds and the widgets
        /// poll at 1 Hz or slower, so the pipe is idle essentially all the time.
        /// </summary>
        private static async Task AcceptLoop(CancellationToken token)
        {
            HashSet<string> granted = FindWidgetPackageSids();

            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server;
                lock (Sync) { server = _currentServer; }

                if (server is null) return;

                bool connected = false;

                try
                {
                    using (CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        wait.CancelAfter(PipeRefreshSeconds * 1000);
                        await server.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
                        connected = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;

                    // Timed out rather than cancelled. The instance is deliberately NOT
                    // disposed and recreated: .NET sets FILE_FLAG_FIRST_PIPE_INSTANCE, so
                    // the kernel still holds the pipe name for a moment after disposal and
                    // the replacement create fails with ERROR_ACCESS_DENIED - leaving
                    // clients with ERROR_FILE_NOT_FOUND or ERROR_PIPE_BUSY in the gap.
                    //
                    // Instead the security descriptor is refreshed in place, which picks up
                    // a widget package that was installed after G-Helper started.
                    HashSet<string> current = FindWidgetPackageSids();
                    if (!granted.SetEquals(current))
                    {
                        granted = current;
                        if (RefreshPipeSecurity(server, granted))
                        {
                            Logger.WriteLine("Game Bar bridge: refreshed the pipe security descriptor");
                        }
                    }

                    continue;
                }
                catch (Exception ex)
                {
                    // The instance is unusable (a client may have disconnected in a way that
                    // leaves it broken). Replace it, tolerating the name being briefly held.
                    Logger.WriteLine("Game Bar bridge: pipe instance unusable (" + ex.Message + ") - recreating");
                    lock (Sync) { _currentServer = null; }
                    try { server.Dispose(); } catch { }

                    for (int attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++)
                    {
                        try
                        {
                            NamedPipeServerStream replacement = CreateInstance(granted);
                            lock (Sync) { _currentServer = replacement; }
                            break;
                        }
                        catch (Exception)
                        {
                            try { await Task.Delay(250).ConfigureAwait(false); } catch { }
                        }
                    }

                    continue;
                }

                if (!connected) continue;

                lock (Sync) { _clientCount++; }

                // The widget wants load, temperatures and power. Tell the overlay so it
                // collects them too - that keeps the total at one poll per second instead
                // of the widget and the overlay each polling.
                HardwareControl.widgetConnected = true;
                Program.hardwareOverlay?.ApplySensorFlags();

                try
                {
                    if (!IsSameUser(server))
                    {
                        Logger.WriteLine("Game Bar bridge: rejected a client running as another user");
                    }
                    else
                    {
                        await HandleClientAsync(server, token).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Client vanished or the app is shutting down.
                }
                finally
                {
                    lock (Sync)
                    {
                        if (_clientCount > 0) _clientCount--;
                    }

                    if (_clientCount == 0)
                    {
                        HardwareControl.widgetConnected = false;
                        Program.hardwareOverlay?.ApplySensorFlags();
                    }
                }
            }
        }

        /// <summary>
        /// The package SIDs of every installed copy of the widget.
        ///
        /// An AppContainer token does not honour an "Everyone" ACE - its access check
        /// compares the DACL against the package SID and capability SIDs in the token. So the
        /// widget's package SID has to be granted explicitly, which is what the Game Bar
        /// documentation means by granting "your desired SID (package SID or app capability
        /// SID)".
        ///
        /// The SID is discoverable in the registry:
        ///     HKLM\SOFTWARE\Microsoft\SecurityManager\CapAuthz\ApplicationsEx\&lt;full name&gt;
        ///         PackageSid
        ///
        /// (The documentation spells that key "ApplicationEx"; on this machine it is
        /// "ApplicationsEx". Both are tried.)
        /// </summary>
        private static HashSet<string> FindWidgetPackageSids()
        {
            HashSet<string> sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    foreach (string subKey in new[]
                    {
                        @"SOFTWARE\Microsoft\SecurityManager\CapAuthz\ApplicationsEx",
                        @"SOFTWARE\WOW6432Node\Microsoft\SecurityManager\CapAuthz\ApplicationsEx"
                    })
                    {
                        using (RegistryKey exKey = root.OpenSubKey(subKey))
                        {
                            if (exKey == null) continue;

                            foreach (string name in exKey.GetSubKeyNames())
                            {
                                if (!name.StartsWith(WidgetPackageName + "_", StringComparison.OrdinalIgnoreCase)) continue;

                                using (RegistryKey package = exKey.OpenSubKey(name))
                                {
                                    string? sid = package?.GetValue("PackageSid") as string;
                                    if (!string.IsNullOrEmpty(sid)) sids.Add(sid);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Game Bar bridge: could not resolve the widget package SID: " + ex.Message);
            }

            return sids;
        }

        private static PipeSecurity BuildPipeSecurity(HashSet<string> widgetPackageSids)
        {
            // The pipe DACL has to cover two different kinds of client:
            //
            //  * An AppContainer (the widget) does NOT honour an "Everyone" ACE. Its
            //    token carries the package SID and capability SIDs, and access checks
            //    compare against those, so a WorldSid rule alone is ignored.
            //  * A normal desktop client is covered by the WorldSid rule.
            //
            // The Game Bar documentation says the same thing: grant
            // SECURITY_WORLD_SID_AUTHORITY "as well as your desired SID (package SID or
            // app capability SID)".
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            foreach (string sid in widgetPackageSids)
            {
                try
                {
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(sid),
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));
                    Logger.WriteLine("Game Bar bridge: granting pipe access to " + sid);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Game Bar bridge: bad package SID " + sid + ": " + ex.Message);
                }
            }

            return security;
        }

        /// <summary>
        /// Replaces the DACL on a live pipe instance.
        ///
        /// The pipe instance cannot simply be disposed and recreated to pick up a new
        /// descriptor: .NET sets FILE_FLAG_FIRST_PIPE_INSTANCE, so the kernel still holds
        /// the pipe name for a moment after disposal and the replacement create fails with
        /// ERROR_ACCESS_DENIED. Clients hitting that gap see ERROR_FILE_NOT_FOUND or
        /// ERROR_PIPE_BUSY. Updating the descriptor in place avoids the gap entirely.
        /// </summary>
        private static bool RefreshPipeSecurity(NamedPipeServerStream server, HashSet<string> widgetPackageSids)
        {
            try
            {
                PipeSecurity security = BuildPipeSecurity(widgetPackageSids);

                byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
                IntPtr buffer = Marshal.AllocHGlobal(descriptor.Length);
                try
                {
                    Marshal.Copy(descriptor, 0, buffer, descriptor.Length);

                    IntPtr dacl;
                    bool present;
                    bool defaulted;
                    if (!GetSecurityDescriptorDacl(buffer, out dacl, out present, out defaulted) || !present)
                        return false;

                    return SetSecurityInfo(
                        server.SafePipeHandle.DangerousGetHandle(),
                        SE_FILE_OBJECT,
                        DACL_SECURITY_INFORMATION,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        dacl,
                        IntPtr.Zero) == 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Game Bar bridge: could not refresh the pipe security descriptor: " + ex.Message);
                return false;
            }
        }

        private static NamedPipeServerStream CreateInstance(HashSet<string> widgetPackageSids)
        {
            return NamedPipeServerStreamAcl.Create(
                GameBar.GameBarProtocol.PipeName,
                PipeDirection.InOut,
                MaxClients,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                BuildPipeSecurity(widgetPackageSids),
                HandleInheritability.None);
        }

        /// <summary>
        /// The pipe DACL has to grant Everyone, because an AppContainer token carries
        /// neither the interactive user's SID nor any other SID the desktop process could
        /// predict. To stop another local user from driving this channel, the connecting
        /// process is resolved and its token user compared against ours.
        /// </summary>
        private static bool IsSameUser(NamedPipeServerStream server)
        {
            uint processId;
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out processId) || processId == 0)
                return false;

            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (process == IntPtr.Zero) return false;

                if (!OpenProcessToken(process, TOKEN_QUERY, out token) || token == IntPtr.Zero) return false;

                int length;
                GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out length);
                if (length <= 0) return false;

                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, 1, buffer, length, out length)) return false;

                // TOKEN_USER starts with a SID_AND_ATTRIBUTES whose first member is the
                // PSID. Note SecurityIdentifier has no parameterless constructor, so
                // Marshal.PtrToStructure throws MissingMethodException - read the pointer
                // instead.
                IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
                if (sidPtr == IntPtr.Zero) return false;

                var sid = new SecurityIdentifier(sidPtr);
                SecurityIdentifier? current = WindowsIdentity.GetCurrent().User;

                return sid.Equals(current);
            }
            catch (Exception)
            {
                // If the check itself fails, deny. The widget simply shows offline and
                // retries.
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
        {
            // One reader/writer per connection, left open so the pipe instance survives for
            // the next client.
            using (StreamReader reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true))
            using (StreamWriter writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true))
            {
                writer.AutoFlush = false;
                writer.NewLine = "\n";

                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;

                    line = line.Trim();
                    if (line.Length == 0) continue;

                    await writer.WriteLineAsync(HandleRequest(line)).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }

        private static string HandleRequest(string line)
        {
            try
            {
                // RemoveEmptyEntries so a stray double space cannot shift the arguments.
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return ReplyError("empty request");

                switch (parts[0].ToUpperInvariant())
                {
                    case GameBar.GameBarProtocol.VerbPing:
                        return ReplyOk(GameBar.GameBarProtocol.KeyVersion + "=" + GameBar.GameBarProtocol.Version);

                    case GameBar.GameBarProtocol.VerbState:
                        return ReplyOk(BuildState());

                    case GameBar.GameBarProtocol.VerbSet:
                        return HandleSet(parts);

                    default:
                        return ReplyError("unknown verb");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Game Bar bridge request failed: " + ex.Message);
                return ReplyError("internal error");
            }
        }

        private static string HandleSet(string[] parts)
        {
            if (parts.Length < 3) return ReplyError("missing argument");

            switch (parts[1].ToLowerInvariant())
            {
                case GameBar.GameBarProtocol.SetMode: return SetMode(parts[2]);
                case GameBar.GameBarProtocol.SetGpuMode: return SetGpuMode(parts[2]);
                case GameBar.GameBarProtocol.SetOverlay: return SetOverlay(parts[2]);
                case GameBar.GameBarProtocol.SetScreen: return SetScreen(parts[2]);
                case GameBar.GameBarProtocol.SetFnLock: return SetToggle(parts[2], GameBar.GameBarProtocol.SetFnLock, RequestedFnLock());
                case GameBar.GameBarProtocol.SetBatteryFull: return SetToggle(parts[2], GameBar.GameBarProtocol.SetBatteryFull, BatteryControl.chargeFull);
                case GameBar.GameBarProtocol.SetControllerMode: return SetControllerMode(parts[2]);
                case GameBar.GameBarProtocol.SetFpsLimit: return SetFpsLimit(parts[2]);
                case GameBar.GameBarProtocol.SetAutoTdp: return SetAutoTdp(parts[2]);
                case GameBar.GameBarProtocol.SetVibration: return SetVibration(parts[2]);
                default: return ReplyError("unknown key");
            }
        }

        // ── Commands ─────────────────────────────────────────────────────────
        //
        // Every command mutates state owned by the UI thread, so they all hop through the
        // settings form's Invoke. RunOnUi reports whether it actually ran: replying OK to a
        // command that was never applied is worse than replying ERR.

        private static string SetMode(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)) return ReplyError("mode is not a number");
            if (!Modes.Exists(mode)) return ReplyError("unknown mode");

            return RunOnUi(() => Program.modeControl.SetPerformanceMode(mode)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetGpuMode(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gpuMode)) return ReplyError("gpu_mode is not a number");
            if (gpuMode < AsusACPI.GPUModeEco || gpuMode > AsusACPI.GPUModeUltimate) return ReplyError("gpu_mode out of range");

            return RunOnUi(() => Program.gpuControl.SetGPUMode(gpuMode)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetOverlay(string value)
        {
            if (value != "0" && value != "1") return ReplyError("overlay must be 0 or 1");

            // Toggle rather than "set": the widget sends the state it wants and the desktop
            // side owns the current one, so reading it here keeps the two from drifting.
            if ((AppConfig.IsOverlay() ? "1" : "0") == value) return ReplyOk(null);

            return RunOnUi(() => Program.settingsForm.ToggleOverlay()) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        /// <summary>0 = auto, 1 = max refresh rate, 2 = min refresh rate.</summary>
        private static string SetScreen(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int screen)) return ReplyError("screen must be 0, 1 or 2");
            if (screen < 0 || screen > 2) return ReplyError("screen must be 0, 1 or 2");

            // Mirrors the Settings window buttons exactly.
            switch (screen)
            {
                case 0:
                    return RunOnUi(() =>
                    {
                        ScreenControl.SetAutoRefresh(1);
                        ScreenControl.AutoScreen();
                    }) ? ReplyOk(null) : ReplyError("ui unavailable");

                case 1:
                    return RunOnUi(() =>
                    {
                        ScreenControl.SetAutoRefresh(0);
                        ScreenControl.SetScreen(ScreenControl.MAX_REFRESH, 1);
                    }) ? ReplyOk(null) : ReplyError("ui unavailable");

                default:
                    return RunOnUi(() =>
                    {
                        ScreenControl.SetAutoRefresh(0);
                        ScreenControl.SetScreen(ScreenControl.MIN_RATE, 0);
                    }) ? ReplyOk(null) : ReplyError("ui unavailable");
            }
        }

        /// <summary>
        /// Shared by the boolean toggles that have no setter of their own. Applies only when
        /// the requested state differs, because the underlying API is a toggle.
        /// </summary>
        private static string SetToggle(string value, string key, bool current)
        {
            if (value != "0" && value != "1") return ReplyError(key + " must be 0 or 1");
            if ((current ? "1" : "0") == value) return ReplyOk(null);

            bool applied = key == GameBar.GameBarProtocol.SetFnLock
                ? RunOnUi(() => InputDispatcher.ToggleFnLock())
                : RunOnUi(() => BatteryControl.ToggleBatteryLimitFull());

            return applied ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetControllerMode(string value)
        {
            if (!AppConfig.IsAlly()) return ReplyError("controller mode is only available on ROG Ally");

            switch (value.ToLowerInvariant())
            {
                case "auto": return ApplyControllerMode(ControllerMode.Auto);
                case "gamepad": return ApplyControllerMode(ControllerMode.Gamepad);
                case "mouse": return ApplyControllerMode(ControllerMode.Mouse);
                case "skip": return ApplyControllerMode(ControllerMode.Skip);
                default: return ReplyError("unknown controller mode");
            }
        }

        private static string ApplyControllerMode(ControllerMode mode)
        {
            return RunOnUi(() => Program.allyControl.SetModeDirect(mode)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetFpsLimit(string value)
        {
            if (!AppConfig.IsAlly()) return ReplyError("fps limit is only available on ROG Ally");
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit)) return ReplyError("fps_limit is not a number");

            return RunOnUi(() => Program.allyControl.SetFpsLimit(limit)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetAutoTdp(string value)
        {
            if (!AppConfig.IsAlly()) return ReplyError("auto tdp is only available on ROG Ally");
            if (value != "0" && value != "1") return ReplyError("auto_tdp must be 0 or 1");

            bool enabled = value == "1";
            return RunOnUi(() => Program.allyControl.SetAutoTdp(enabled)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static string SetVibration(string value)
        {
            if (!AppConfig.IsAlly()) return ReplyError("vibration is only available on ROG Ally");
            if (value != "0" && value != "1") return ReplyError("vibration must be 0 or 1");

            bool enhanced = value == "1";
            return RunOnUi(() => AllyControl.SetVibrationEnhanced(enhanced)) ? ReplyOk(null) : ReplyError("ui unavailable");
        }

        private static bool RequestedFnLock() => AppConfig.Is("fn_lock");

        /// <summary>
        /// Runs on the UI thread. Returns false when there is no live UI to run on, so the
        /// caller can report failure rather than silently claiming success.
        /// </summary>
        private static bool RunOnUi(Action action)
        {
            Control? form = Program.settingsForm;
            if (form is null || form.IsDisposed || !form.IsHandleCreated)
            {
                Logger.WriteLine("Game Bar bridge: no UI available for command");
                return false;
            }

            try
            {
                if (form.InvokeRequired) form.Invoke(action);
                else action();

                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Game Bar bridge command failed: " + ex.Message);
                return false;
            }
        }

        // ── Replies ─────────────────────────────────────────────────────────

        private static string ReplyOk(string? payload)
        {
            return GameBar.GameBarProtocol.Reply(GameBar.GameBarProtocol.ReplyOk, payload);
        }

        private static string ReplyError(string message)
        {
            return GameBar.GameBarProtocol.Reply(GameBar.GameBarProtocol.ReplyError, message);
        }

        // ── State ───────────────────────────────────────────────────────────
        //
        // Only settings the widget can change. Sensor values are deliberately absent: they
        // are owned by G-Helper's existing pollers and reading them from here would add a
        // second, competing poll.

        private static string BuildState()
        {
            RefreshSensors();

            List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>(40);

            void Add(string key, string? val) => values.Add(new KeyValuePair<string, string>(key, val ?? ""));

            bool isAlly = AppConfig.IsAlly();

            Add(GameBar.GameBarProtocol.KeyVersion, Inv(GameBar.GameBarProtocol.Version));
            Add(GameBar.GameBarProtocol.KeyModel, AppConfig.GetModelShort());
            Add(GameBar.GameBarProtocol.KeyPower, Program.currentSource.ToString());
            Add(GameBar.GameBarProtocol.KeyIsAlly, isAlly ? "1" : "0");

            Add(GameBar.GameBarProtocol.KeyMode, Inv(Modes.GetCurrent()));
            Add(GameBar.GameBarProtocol.KeyModeName, Modes.GetCurrentName());

            List<int> modes = Modes.GetList();
            List<string> modeIds = new List<string>();
            List<string> modeNames = new List<string>();
            foreach (int id in modes)
            {
                modeIds.Add(Inv(id));
                modeNames.Add(GameBar.GameBarProtocol.Sanitize(Modes.GetName(id)));
            }
            Add(GameBar.GameBarProtocol.KeyModes, string.Join(",", modeIds));
            Add(GameBar.GameBarProtocol.KeyModeNames, string.Join(",", modeNames));

            Add(GameBar.GameBarProtocol.KeyGpuMode, Inv(GPUModeControl.gpuMode));

            Add(GameBar.GameBarProtocol.KeyOverlay, AppConfig.IsOverlay() ? "1" : "0");
            Add(GameBar.GameBarProtocol.KeyFnLock, RequestedFnLock() ? "1" : "0");
            Add(GameBar.GameBarProtocol.KeyBatteryFull, BatteryControl.chargeFull ? "1" : "0");

            // Live numbers. The overlay owns the sensor cache when it is running; when it is
            // not, the bridge reads it itself so the widget is never blank.
            Add(GameBar.GameBarProtocol.KeyFps, Program.hardwareOverlay is null ? "-1" : Program.hardwareOverlay.CurrentFps.ToString(CultureInfo.InvariantCulture));
            Add(GameBar.GameBarProtocol.KeyCpuTemp, IntOrUnknown(HardwareControl.cpuTemp));
            Add(GameBar.GameBarProtocol.KeyGpuTemp, IntOrUnknown(HardwareControl.gpuTemp));
            Add(GameBar.GameBarProtocol.KeyCpuLoad, IntOrUnknown(HardwareControl.cpuUsage));
            Add(GameBar.GameBarProtocol.KeyGpuLoad, IntOrUnknown(HardwareControl.gpuUsage));
            Add(GameBar.GameBarProtocol.KeyMemLoad, IntOrUnknown(HardwareControl.ramUsage));
            Add(GameBar.GameBarProtocol.KeyFocusedApp, FocusedApp());

            Add(GameBar.GameBarProtocol.KeyScreenAuto, AppConfig.Get("screen_auto") != 0 ? "1" : "0");
            Add(GameBar.GameBarProtocol.KeyScreenMin, Inv(ScreenControl.MIN_RATE));
            Add(GameBar.GameBarProtocol.KeyScreenMax, ScreenControl.MAX_RATE > 0 ? Inv(ScreenControl.MAX_RATE) : "");

            if (isAlly)
            {
                Add(GameBar.GameBarProtocol.KeyControllerMode, AllyControl.CurrentMode.ToString().ToLowerInvariant());

                List<string> controllerModes = new List<string>();
                foreach (ControllerMode option in AllyControl.ControllerModeOptions)
                    controllerModes.Add(option.ToString().ToLowerInvariant());
                Add(GameBar.GameBarProtocol.KeyControllerModes, string.Join(",", controllerModes));

                Add(GameBar.GameBarProtocol.KeyFpsLimit, Inv(AllyControl.CurrentFpsLimit));
                Add(GameBar.GameBarProtocol.KeyAutoTdp, AllyControl.CurrentAutoTDP ? "1" : "0");
                Add(GameBar.GameBarProtocol.KeyTdp, AllyControl.CurrentTdp > 0 ? Inv(AllyControl.CurrentTdp) : "-1");
                Add(GameBar.GameBarProtocol.KeyVibration, AllyControl.VibrationEnhanced ? "1" : "0");

                List<string> fpsOptions = new List<string>();
                foreach (int option in AllyControl.FpsLimitOptions)
                    fpsOptions.Add(Inv(option));
                Add(GameBar.GameBarProtocol.KeyFpsLimitOptions, string.Join(",", fpsOptions));
            }
            else
            {
                Add(GameBar.GameBarProtocol.KeyControllerMode, "");
                Add(GameBar.GameBarProtocol.KeyControllerModes, "");
                Add(GameBar.GameBarProtocol.KeyFpsLimit, "");
                Add(GameBar.GameBarProtocol.KeyFpsLimitOptions, "");
                Add(GameBar.GameBarProtocol.KeyAutoTdp, "0");
                Add(GameBar.GameBarProtocol.KeyTdp, "-1");
                Add(GameBar.GameBarProtocol.KeyVibration, "0");
            }

            return GameBar.GameBarProtocol.FormatState(values);
        }

        /// <summary>
        /// Makes sure the sensor cache is fresh.
        ///
        /// When the hardware overlay is running it already polls once a second - and
        /// ApplySensorFlags has been told that a widget is connected, so it collects the
        /// extra readings the widget needs. Reading the cache here is then free.
        ///
        /// When the overlay is not running the bridge has to poll itself, otherwise the
        /// widget would show nothing at all.
        /// </summary>
        private static void RefreshSensors()
        {
            if (Program.hardwareOverlay is not null && Program.hardwareOverlay.IsActive) return;

            try
            {
                HardwareControl.ReadSensorsOverlay(
                    fans: true,
                    usage: true,
                    memory: true,
                    power: true,
                    battery: true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Game Bar bridge sensor read failed: " + ex.Message);
            }
        }

        /// <summary>Formats a nullable reading, using -1 for "not available".</summary>
        private static string IntOrUnknown(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-1";
        }

        /// <summary>Formats a nullable temperature, using -1 for "not available".</summary>
        private static string IntOrUnknown(float? value)
        {
            return value.HasValue
                ? ((int)Math.Round(value.Value)).ToString(CultureInfo.InvariantCulture)
                : "-1";
        }

        /// <summary>
        /// The application in the foreground, so the widget can show what is being played.
        /// The shell and other desktop furniture are filtered out - they are never the
        /// interesting answer.
        /// </summary>
        private static string FocusedApp()
        {
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return "";

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (processId == 0 || processId == (uint)Environment.ProcessId) return "";

                using (Process process = Process.GetProcessById((int)processId))
                {
                    string name = process.ProcessName;
                    if (string.IsNullOrEmpty(name)) return "";
                    if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("GameBar", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase)) return "";
                    if (name.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase)) return "";

                    return name;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}