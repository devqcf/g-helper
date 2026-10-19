using System;
using System.Collections.Generic;
using System.Text;

namespace GHelper.GameBar
{
    /// <summary>
    /// Wire protocol shared by the G-Helper desktop process and the packaged Xbox
    /// Game Bar widget (see app/GameBar/GHelper.GameBar.csproj, which links this file).
    ///
    /// Because the same source is compiled into both projects, it has to stay inside the
    /// intersection of the two toolchains:
    ///   * C# 7.3 only - no target-typed new, no nullable reference types, no switch
    ///     expressions. The UWP project builds through .NET Native with the legacy
    ///     (non SDK-style) csproj, which does not opt into a modern LangVersion.
    ///   * No System.Net.Sockets, no JSON, no LINQ-heavy helpers. Plain strings,
    ///     StringBuilder and Dictionary are available in both.
    ///
    /// Transport is a UTF-8, newline terminated, request/response protocol over a named
    /// pipe, which is what the Game Bar documentation prescribes for widget-to-desktop
    /// communication.
    ///
    /// Two alternatives were ruled out, both against Microsoft documentation:
    ///
    ///  * Loopback TCP does not work. "Network communications using an IP loopback
    ///    address cannot be used for interprocess communication (between two different
    ///    apps) in a Windows Runtime app" and "Loopback is permitted only for
    ///    development purposes. Usage by a Windows Runtime app installed outside of
    ///    Visual Studio is not permitted." Visual Studio auto-grants a loopback exemption
    ///    to apps it deploys, so a TCP bridge works perfectly while you develop and then
    ///    fails for every real user.
    ///    https://learn.microsoft.com/en-us/previous-versions/windows/apps/hh780593
    ///
    ///  * System.IO.Pipes.NamedPipeClientStream does not work from AppContainer
    ///    ("not supported in app container", dotnet/runtime#25764). The widget therefore
    ///    opens the pipe with the Win32 CreateFile API, which AppContainer permits as long
    ///    as the pipe's DACL grants access - hence the explicit security descriptor the
    ///    server installs.
    ///
    /// Security note: the DACL has to grant Everyone, because an AppContainer token
    /// carries neither the interactive user's SID nor any other SID the desktop process
    /// could predict. To stop a different local user from driving this channel, the server
    /// verifies the connecting process runs as the same user before serving any request.
    /// </summary>
    public static class GameBarProtocol
    {
        /// <summary>Bumped whenever the state payload or SET semantics change.</summary>
        public const int Version = 1;

        /// <summary>
        /// Pipe name only, not a path. Deliberately not user configurable: the widget is
        /// sandboxed and cannot read G-Helper's AppConfig, so an override on the desktop
        /// side would silently desynchronise the two ends.
        /// </summary>
        public const string PipeName = "GHelperGameBar";

        // ── Request verbs ────────────────────────────────────────────────────
        public const string VerbPing = "PING";
        public const string VerbState = "STATE";
        public const string VerbSet = "SET";

        // ── Reply prefixes ───────────────────────────────────────────────────
        public const string ReplyOk = "OK";
        public const string ReplyError = "ERR";

        // ── SET <key> <value> targets ─────────────────────────────────────────
        public const string SetMode = "mode";
        public const string SetGpuMode = "gpu_mode";
        public const string SetOverlay = "overlay";

        /// <summary>0 = auto, 1 = max refresh rate, 2 = min refresh rate.</summary>
        public const string SetScreen = "screen";

        public const string SetFnLock = "fn_lock";
        public const string SetBatteryFull = "battery_full";

        /// <summary>auto | gamepad | mouse | skip</summary>
        public const string SetControllerMode = "controller_mode";

        /// <summary>Frame cap in fps, or -1 for uncapped.</summary>
        public const string SetFpsLimit = "fps_limit";

        /// <summary>0 = manual TDP, 1 = let the frame rate drive the TDP.</summary>
        public const string SetAutoTdp = "auto_tdp";

        /// <summary>0 = normal vibration, 1 = enhanced.</summary>
        public const string SetVibration = "vibration";

        // ── STATE keys ───────────────────────────────────────────────────────
        public const string KeyVersion = "ver";
        public const string KeyModel = "model";
        public const string KeyMode = "mode";
        public const string KeyModeName = "mode_name";

        /// <summary>Comma separated list of every selectable mode id, e.g. "2,0,1,3".</summary>
        public const string KeyModes = "modes";

        /// <summary>Comma separated mode names, index aligned with <see cref="KeyModes"/>.</summary>
        public const string KeyModeNames = "mode_names";

        public const string KeyGpuMode = "gpu_mode";
        public const string KeyPower = "power";

        /// <summary>Whether G-Helper's on-screen hardware overlay is currently enabled.</summary>
        public const string KeyOverlay = "overlay";

        /// <summary>1 when the panel switches refresh rate on AC/battery changes.</summary>
        public const string KeyScreenAuto = "screen_auto";

        /// <summary>Lowest refresh rate the panel supports, in Hz.</summary>
        public const string KeyScreenMin = "screen_min";

        /// <summary>Highest refresh rate the panel supports, in Hz, or "" if unknown.</summary>
        public const string KeyScreenMax = "screen_max";

        public const string KeyFnLock = "fn_lock";

        /// <summary>1 when the battery charge limit is pinned to 100%.</summary>
        public const string KeyBatteryFull = "battery_full";

        /// <summary>auto | gamepad | mouse | skip</summary>
        public const string KeyControllerMode = "controller_mode";

        /// <summary>Comma separated subset of controller modes this device supports.</summary>
        public const string KeyControllerModes = "controller_modes";

        /// <summary>Current FPS frame cap, or -1 when uncapped.</summary>
        public const string KeyFpsLimit = "fps_limit";

        /// <summary>Comma separated frame caps offered, -1 meaning uncapped.</summary>
        public const string KeyFpsLimitOptions = "fps_limit_options";

        /// <summary>1 on ASUS Ally, where controller mode and FPS cap are available.</summary>
        public const string KeyIsAlly = "is_ally";

        /// <summary>Frames per second of the foreground game, or -1 when unknown.</summary>
        public const string KeyFps = "fps";

        /// <summary>CPU load percentage, or -1 when unknown.</summary>
        public const string KeyCpuLoad = "cpu_load";

        /// <summary>GPU load percentage, or -1 when unknown.</summary>
        public const string KeyGpuLoad = "gpu_load";

        /// <summary>Memory load percentage, or -1 when unknown.</summary>
        public const string KeyMemLoad = "mem_load";

        /// <summary>CPU temperature in degrees C, or -1 when unknown.</summary>
        public const string KeyCpuTemp = "cpu_temp";

        /// <summary>GPU temperature in degrees C, or -1 when unknown.</summary>
        public const string KeyGpuTemp = "gpu_temp";

        /// <summary>1 when the TDP is being driven automatically from the frame rate.</summary>
        public const string KeyAutoTdp = "auto_tdp";

        /// <summary>Current TDP in watts, or -1 when unknown.</summary>
        public const string KeyTdp = "tdp";

        /// <summary>1 when the controller uses the enhanced vibration profile.</summary>
        public const string KeyVibration = "vibration";

        /// <summary>Name of the application in the foreground, or empty.</summary>
        public const string KeyFocusedApp = "focused_app";

        // Sensor readings are intentionally NOT part of this protocol. G-Helper refreshes them
        // on its own timers and the widget has no need to drive a second, competing poll.

        /// <summary>
        /// Strips the characters that would break the key=value;key=value payload.
        /// Sensor names come from the firmware and are not guaranteed to be clean.
        /// </summary>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ';' || c == '=' || c == '\r' || c == '\n')
                    sb.Append('-');
                else if (c < 32)
                    sb.Append(' ');
                else
                    sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>Builds "key=value;key=value" from an ordered key/value list.</summary>
        public static string FormatState(IEnumerable<KeyValuePair<string, string>> values)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in values)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(entry.Key);
                sb.Append('=');
                sb.Append(Sanitize(entry.Value));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses the payload of an "OK &lt;payload&gt;" STATE reply. Tolerates a missing
        /// value, unknown keys and duplicated separators so a newer desktop build never
        /// breaks an older widget.
        /// </summary>
        public static Dictionary<string, string> ParseState(string payload)
        {
            Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(payload)) return state;

            string[] pairs = payload.Split(';');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;

                int split = pairs[i].IndexOf('=');
                if (split <= 0) continue;

                string key = pairs[i].Substring(0, split).Trim();
                string value = pairs[i].Substring(split + 1).Trim();
                if (key.Length > 0) state[key] = value;
            }

            return state;
        }

        /// <summary>Serialises a request line, e.g. Request("SET", "mode", "2").</summary>
        public static string Request(string verb, params string[] args)
        {
            if (args == null || args.Length == 0) return verb;

            StringBuilder sb = new StringBuilder(verb);
            for (int i = 0; i < args.Length; i++)
            {
                sb.Append(' ');
                sb.Append(args[i] == null ? "" : args[i]);
            }

            return sb.ToString();
        }

        /// <summary>Serialises a reply line, e.g. Reply("OK", "ver=1;model=ROG Flow").</summary>
        public static string Reply(string status, string payload)
        {
            return string.IsNullOrEmpty(payload) ? status : status + " " + payload;
        }

        /// <summary>
        /// Splits "OK payload" / "ERR message" into its status and payload parts.
        /// Returns false for anything unrecognised.
        /// </summary>
        public static bool TryParseReply(string line, out string status, out string payload)
        {
            status = "";
            payload = "";
            if (string.IsNullOrEmpty(line)) return false;

            line = line.Trim();
            if (line.Length == 0) return false;

            int space = line.IndexOf(' ');
            status = space < 0 ? line : line.Substring(0, space);
            payload = space < 0 ? "" : line.Substring(space + 1).Trim();

            return status.Equals(ReplyOk, StringComparison.OrdinalIgnoreCase)
                || status.Equals(ReplyError, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryReadInt(Dictionary<string, string> state, string key, out int value)
        {
            value = 0;
            if (state == null) return false;

            string raw;
            if (!state.TryGetValue(key, out raw)) return false;

            return int.TryParse(raw, out value);
        }

        public static int ReadInt(Dictionary<string, string> state, string key, int fallback)
        {
            int parsed;
            return TryReadInt(state, key, out parsed) ? parsed : fallback;
        }

        public static bool ReadBool(Dictionary<string, string> state, string key, bool fallback)
        {
            return ReadInt(state, key, fallback ? 1 : 0) != 0;
        }

        public static string ReadString(Dictionary<string, string> state, string key, string fallback)
        {
            if (state == null) return fallback;

            string raw;
            if (!state.TryGetValue(key, out raw) || raw.Length == 0) return fallback;

            return raw;
        }
    }
}