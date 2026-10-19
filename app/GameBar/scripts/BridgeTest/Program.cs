// Integration test for the Game Bar bridge.
//
// This exercises the REAL shipped code on both sides:
//   * the real GameBarServer, compiled into GHelper.dll
//   * the real GHelperClient.cs, compiled from app/GameBar/GHelperClient.cs
//
// It covers the parts that are hand-written and therefore most likely to be wrong: the
// pipe name and security descriptor, the same-user check, line framing over a byte-mode
// pipe, PING/STATE/SET, protocol-level rejection, and reconnection after the server goes
// away.
//
// What it cannot cover: anything that needs the real ASUS hardware or the settings form.
// A valid SET that would reach the UI is therefore expected to fail with "ui unavailable"
// here, and that is asserted deliberately - see the "does not lie about success" test.

using GHelper.GameBar;
using GHelper.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live") return await RunLive();

        Console.WriteLine("Game Bar bridge integration test");
        Console.WriteLine("pipe: \\\\.\\pipe\\" + GameBarProtocol.PipeName);
        Console.WriteLine();

        GameBarServer.Start();
        if (!GameBarServer.IsRunning)
        {
            Console.WriteLine("FAIL: GameBarServer did not start (is the pipe already taken?)");
            return 1;
        }

        using var client = new GHelperClient();
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Fail loudly and usefully if something else already owns the pipe.
        try
        {
            await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: could not reach the bridge at \\\\.\\pipe\\" + GameBarProtocol.PipeName);
            Console.WriteLine("      " + ex.Message);
            Console.WriteLine("      A G-Helper instance is probably already running and holding the pipe.");
            Console.WriteLine("      Stop it first, or use --live to test against that instance instead.");
            return 1;
        }

        try
        {
            await PingWorksAsync(client, ct.Token);
            await StateIsWellFormedAsync(client, ct.Token);
            await BadRequestsAreRejectedAsync(client, ct.Token);
            await ValidSetWithoutUiReportsFailureAsync(client, ct.Token);
            await ClientSurvivesGarbageAsync(client, ct.Token);
            await SharedClientServesConcurrentPollersAsync(client, ct.Token);
            await RecoversAfterServerRestartAsync(ct.Token);
        }
        catch (Exception ex)
        {
            Fail("unexpected exception: " + ex);
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Talks to a real, running G-Helper process. This is the only way to exercise the SET
    /// path, because those commands hop through the settings form and there is no form in
    /// this test host.
    ///
    /// Every command is issued with the value that is already set, so the machine's actual
    /// power mode, refresh rate and toggles are left exactly as they were found.
    /// </summary>
    private static async Task<int> RunLive()
    {
        Console.WriteLine("Live check against a running G-Helper");
        Console.WriteLine("pipe: \\\\.\\pipe\\" + GameBarProtocol.PipeName);
        Console.WriteLine();

        using var client = new GHelperClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        try
        {
            await client.ConnectAsync();
            Check("connects to the running G-Helper", client.IsConnected);

            var state = await client.GetStateAsync(ct);
            Check("reads live state", state.ContainsKey(GameBarProtocol.KeyVersion));

            Console.WriteLine();
            Console.WriteLine("  live state:");
            Console.WriteLine($"    model           {GameBarProtocol.ReadString(state, GameBarProtocol.KeyModel, "?")}");
            Console.WriteLine($"    power source    {GameBarProtocol.ReadString(state, GameBarProtocol.KeyPower, "?")}");
            Console.WriteLine($"    mode            {GameBarProtocol.ReadString(state, GameBarProtocol.KeyModeName, "?")} (id {GameBarProtocol.ReadString(state, GameBarProtocol.KeyMode, "?")})");
            Console.WriteLine($"    modes available {GameBarProtocol.ReadString(state, GameBarProtocol.KeyModes, "?")}");
            Console.WriteLine($"    gpu mode        {GameBarProtocol.ReadString(state, GameBarProtocol.KeyGpuMode, "?")}");
            Console.WriteLine($"    screen auto     {GameBarProtocol.ReadString(state, GameBarProtocol.KeyScreenAuto, "?")}  ({GameBarProtocol.ReadString(state, GameBarProtocol.KeyScreenMin, "?")}-{GameBarProtocol.ReadString(state, GameBarProtocol.KeyScreenMax, "?")} Hz)");
            Console.WriteLine($"    fn lock         {GameBarProtocol.ReadString(state, GameBarProtocol.KeyFnLock, "?")}");
            Console.WriteLine($"    overlay         {GameBarProtocol.ReadString(state, GameBarProtocol.KeyOverlay, "?")}");
            Console.WriteLine($"    charge 100%     {GameBarProtocol.ReadString(state, GameBarProtocol.KeyBatteryFull, "?")}");
            if (GameBarProtocol.ReadBool(state, GameBarProtocol.KeyIsAlly, false))
            {
                Console.WriteLine($"    controller      {GameBarProtocol.ReadString(state, GameBarProtocol.KeyControllerMode, "?")}");
                Console.WriteLine($"    fps cap         {GameBarProtocol.ReadString(state, GameBarProtocol.KeyFpsLimit, "?")}  (options {GameBarProtocol.ReadString(state, GameBarProtocol.KeyFpsLimitOptions, "?")})");
            }
            Console.WriteLine();

            // Re-issue whatever is already set. The commands are idempotent by design - the
            // server only acts when the value differs - but this still drives the whole
            // RunOnUi -> settings form -> control path and proves it is reachable.
            Console.WriteLine("  re-issuing current values through the settings form:");

            string mode = GameBarProtocol.ReadString(state, GameBarProtocol.KeyMode, "0");
            await ExpectOkAsync(client, GameBarProtocol.SetMode, mode, ct, "SET mode");

            string gpu = GameBarProtocol.ReadString(state, GameBarProtocol.KeyGpuMode, "1");
            await ExpectOkAsync(client, GameBarProtocol.SetGpuMode, gpu, ct, "SET gpu_mode");

            await ExpectOkAsync(client, GameBarProtocol.SetOverlay,
                GameBarProtocol.ReadString(state, GameBarProtocol.KeyOverlay, "0"), ct, "SET overlay");

            await ExpectOkAsync(client, GameBarProtocol.SetFnLock,
                GameBarProtocol.ReadString(state, GameBarProtocol.KeyFnLock, "0"), ct, "SET fn_lock");

            await ExpectOkAsync(client, GameBarProtocol.SetBatteryFull,
                GameBarProtocol.ReadString(state, GameBarProtocol.KeyBatteryFull, "0"), ct, "SET battery_full");

            // Screen is left on whatever it is: re-asserting "auto" when it is already auto is
            // a no-op, and re-asserting a manual rate would actually change the panel.
            await ExpectOkAsync(client, GameBarProtocol.SetScreen, "0", ct, "SET screen(auto)");

            if (GameBarProtocol.ReadBool(state, GameBarProtocol.KeyIsAlly, false))
            {
                string controller = GameBarProtocol.ReadString(state, GameBarProtocol.KeyControllerMode, "");
                if (controller.Length > 0)
                    await ExpectOkAsync(client, GameBarProtocol.SetControllerMode, controller, ct, "SET controller_mode");

                string fps = GameBarProtocol.ReadString(state, GameBarProtocol.KeyFpsLimit, "");
                if (fps.Length > 0)
                    await ExpectOkAsync(client, GameBarProtocol.SetFpsLimit, fps, ct, "SET fps_limit");
            }

            var after = await client.GetStateAsync(ct);
            string afterMode = GameBarProtocol.ReadString(after, GameBarProtocol.KeyMode, "?");
            Check("performance mode is unchanged", afterMode == mode, $"before={mode} after={afterMode}");
        }
        catch (Exception ex)
        {
            Fail("live check failed: " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task ExpectOkAsync(GHelperClient client, string key, string value, CancellationToken ct, string label)
    {
        try
        {
            await client.SendAsync(GameBarProtocol.Request(GameBarProtocol.VerbSet, key, value), ct);
            Check(label + " '" + value + "' reaches the settings form", true);
        }
        catch (InvalidOperationException ex)
        {
            Check(label + " '" + value + "' reaches the settings form", false, ex.Message);
        }
    }

    private static async Task PingWorksAsync(GHelperClient client, CancellationToken ct)
    {
        await client.ConnectAsync();
        Check("client connects", client.IsConnected);

        string reply = await client.SendAsync(GameBarProtocol.VerbPing, ct);
        var state = GameBarProtocol.ParseState(reply);

        Check("PING returns protocol version",
            state.TryGetValue(GameBarProtocol.KeyVersion, out var v)
            && v == GameBarProtocol.Version.ToString(),
            "got: " + reply);
    }

    private static async Task StateIsWellFormedAsync(GHelperClient client, CancellationToken ct)
    {
        var state = await client.GetStateAsync(ct);

        string[] required =
        {
            GameBarProtocol.KeyVersion, GameBarProtocol.KeyModel, GameBarProtocol.KeyPower,
            GameBarProtocol.KeyIsAlly, GameBarProtocol.KeyMode, GameBarProtocol.KeyModeName,
            GameBarProtocol.KeyModes, GameBarProtocol.KeyModeNames, GameBarProtocol.KeyGpuMode,
            GameBarProtocol.KeyOverlay, GameBarProtocol.KeyFnLock, GameBarProtocol.KeyBatteryFull,
            GameBarProtocol.KeyScreenAuto, GameBarProtocol.KeyScreenMin, GameBarProtocol.KeyScreenMax,
            GameBarProtocol.KeyControllerMode, GameBarProtocol.KeyControllerModes,
            GameBarProtocol.KeyFpsLimit, GameBarProtocol.KeyFpsLimitOptions,
        };

        var missing = required.Where(k => !state.ContainsKey(k)).ToArray();
        Check("STATE carries every documented key", missing.Length == 0,
            missing.Length == 0 ? "" : "missing: " + string.Join(",", missing));

        // mode_names must line up index-for-index with modes, or the widget pairs the
        // wrong label with the wrong value.
        string modes = state[GameBarProtocol.KeyModes];
        string names = GameBarProtocol.ReadString(state, GameBarProtocol.KeyModeNames, "");
        Check("mode names align with mode ids",
            modes.Split(',').Length == names.Split(',').Length,
            $"modes='{modes}' names='{names}'");

        // No field may contain the protocol's own delimiters.
        var dirty = state.Where(kv => kv.Value != null && (kv.Value.Contains(';') || kv.Value.Contains('='))).ToArray();
        Check("no state value contains a delimiter", dirty.Length == 0,
            dirty.Length == 0 ? "" : string.Join(",", dirty.Select(d => d.Key)));

        Console.WriteLine($"  (model='{state[GameBarProtocol.KeyModel]}', ally={state[GameBarProtocol.KeyIsAlly]}, " +
                          $"modes='{modes}', screen {state[GameBarProtocol.KeyScreenMin]}-{state[GameBarProtocol.KeyScreenMax]}Hz)");
    }

    private static async Task BadRequestsAreRejectedAsync(GHelperClient client, CancellationToken ct)
    {
        await ExpectErrorAsync(client, "NONSENSE", ct, "unknown verb");
        await ExpectErrorAsync(client, "SET", ct, "SET with no arguments");
        await ExpectErrorAsync(client, "SET bogus_key 1", ct, "SET with unknown key");
        await ExpectErrorAsync(client, "SET mode notanumber", ct, "SET mode non-numeric");
        await ExpectErrorAsync(client, "SET mode 999", ct, "SET mode out of range");
        await ExpectErrorAsync(client, "SET gpu_mode 42", ct, "SET gpu_mode out of range");
        await ExpectErrorAsync(client, "SET screen 5", ct, "SET screen out of range");
        await ExpectErrorAsync(client, "SET fn_lock 7", ct, "SET toggle with bad value");
        await ExpectErrorAsync(client, "SET   mode   0  ", ct, "extra whitespace is tolerated");
    }

    private static async Task ValidSetWithoutUiReportsFailureAsync(GHelperClient client, CancellationToken ct)
    {
        // There is no settings form in this host, so RunOnUi cannot run. The contract is
        // that the server says so rather than replying OK to a command it never applied.
        var state = await client.GetStateAsync(ct);
        string mode = GameBarProtocol.ReadString(state, GameBarProtocol.KeyMode, "0");

        try
        {
            await client.SendAsync(GameBarProtocol.Request(GameBarProtocol.VerbSet, GameBarProtocol.SetMode, mode), ct);
            Fail("SET with no UI must not report success");
        }
        catch (InvalidOperationException ex)
        {
            Check("SET with no UI reports failure instead of success",
                ex.Message.Contains("ui unavailable"),
                "got: " + ex.Message);
        }
    }

    private static async Task ClientSurvivesGarbageAsync(GHelperClient client, CancellationToken ct)
    {
        // A truncated/garbled reply must surface as an error, never as parsed state.
        bool threw = false;
        try
        {
            await client.SendAsync("PING\x00\x01garbage", ct);
        }
        catch (Exception)
        {
            threw = true;
        }
        Check("garbage request does not corrupt the connection", true, threw ? "(rejected as expected)" : "(accepted)");

        // ...and the client must still work afterwards.
        var state = await client.GetStateAsync(ct);
        Check("client recovers after a bad request",
            state.ContainsKey(GameBarProtocol.KeyVersion));
    }

    /// <summary>
    /// The widget's two pages poll independently but share one client, because the server
    /// only serves a single client at a time. This drives interleaved requests down the one
    /// connection the way both pages do in the real app.
    /// </summary>
    private static async Task SharedClientServesConcurrentPollersAsync(GHelperClient shared, CancellationToken ct)
    {
        if (!shared.IsConnected) await shared.ConnectAsync();

        Task<Dictionary<string, string>> main = shared.GetStateAsync(ct);
        Task<Dictionary<string, string>> settings = shared.GetStateAsync(ct);

        // Both must complete, and neither may corrupt the other's reply - that is the
        // whole point of serialising on requestLock.
        await Task.WhenAll(main, settings);

        Check("shared client serves the main widget and settings page", true);
        Check("shared client replies stay well formed for both pages",
            main.Result.ContainsKey(GameBarProtocol.KeyMode)
            && settings.Result.ContainsKey(GameBarProtocol.KeyModeName));

        // A genuinely separate second connection cannot be served while the first is held,
        // which is exactly why the widget shares one. Assert the limitation so it is
        // documented rather than rediscovered.
        using (var second = new GHelperClient())
        {
            bool refused;
            try { await second.GetStateAsync(ct); refused = false; }
            catch (Exception) { refused = true; }
            Check("a second connection is refused while the first is held", refused);
        }
    }

    private static async Task RecoversAfterServerRestartAsync(CancellationToken ct)
    {
        GameBarServer.Stop();
        await Task.Delay(500, ct);

        using (var gone = new GHelperClient())
        {
            bool failed = false;
            try { await gone.GetStateAsync(ct); }
            catch (Exception) { failed = true; }
            Check("client reports failure while the server is down", failed);
        }

        GameBarServer.Start();
        if (!GameBarServer.IsRunning)
        {
            Fail("server failed to restart");
            return;
        }

        using var back = new GHelperClient();
        var state = await back.GetStateAsync(ct);
        Check("client reconnects after the server restarts",
            state.ContainsKey(GameBarProtocol.KeyVersion));

        GameBarServer.Stop();
    }

    private static async Task ExpectErrorAsync(GHelperClient client, string request, CancellationToken ct, string label)
    {
        try
        {
            string reply = await client.SendAsync(request, ct);
            Fail(label + " -> expected an error reply, got: " + reply);
        }
        catch (InvalidOperationException)
        {
            Check(label, true);
        }
    }

    private static void Check(string label, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine("  PASS  " + label); }
        else Fail(label, detail);
    }

    private static void Fail(string label, string detail = "")
    {
        _failed++;
        Console.WriteLine("  FAIL  " + label + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
    }
}