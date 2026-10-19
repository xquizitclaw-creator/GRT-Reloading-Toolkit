using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GrtPluginKit.Ipc;

/// <summary>Scalar results of one GRT simulation (the charge/curve currently shown in the tab).</summary>
public sealed class GrtResults
{
    public double? MuzzleVelocityMps { get; init; }
    public double? MaxPressure { get; init; }
    public string MaxPressureUnit { get; init; } = "";
    public double? BarrelTimeMs { get; init; }
    /// <summary>Always null against a real GRT 2021.2030 response as of 2026-09-14 - no field named
    /// "BurnRatio" (or any obvious alternative) appears anywhere in a live, complete (non-truncated)
    /// Get_TabResults payload, even though GRT's own report macros and CSV export both use this exact
    /// name. Likely only derivable from the P/V/t chunk stream (not read by this client), not exposed
    /// as a flat scalar. Kept for API shape / in case a different GRT version or load state does
    /// expose it - don't assume it works without re-verifying against a live response first.</summary>
    public double? BurnRatio { get; init; }
    /// <summary>Same caveat as <see cref="BurnRatio"/>: always null against a real live response as of
    /// 2026-09-14, despite "LoadRatio" being a real GRT field name elsewhere (report macros, CSV
    /// export). Not confirmed obtainable via Get_TabResults.</summary>
    public double? LoadRatio { get; init; }
    /// <summary>GRT's own classic (generic, length+caliber-only) optimal barrel time, ms - the
    /// number the "Tempo di canna ottimale (OBT #n)" field in GRT's UI shows. Per GRT's own doku
    /// (obtconcept.txt), this models LONGITUDINAL muzzle-diameter oscillation (Christopher Long's
    /// 2003 theory) - explicitly NOT "barrel whip/harmonic" (transverse bending). See
    /// reference_grt_obt_concept memory before comparing this against a transverse-bending model.</summary>
    public double? OptimalBarrelTimeMs { get; init; }
    /// <summary>The node label GRT prints next to its own OBT value, e.g. "#5" or "#5 ½" - GRT reports
    /// whichever candidate node (from its own length-derived family) is nearest the current charge's
    /// BulletLeadTime10Pmax, confirmed to change across a charge ladder (not a fixed value per barrel).</summary>
    public string OptimalBarrelTimeNode { get; init; } = "";
    /// <summary>Bullet lead time referenced from when pressure crosses 10% of Pmax (IPC field
    /// "BulletLeadTime10Pmax") - the specific timing value GRT's OWN OBT node-matching uses, per
    /// obtconcept.txt. NOT the same number as <see cref="BarrelTimeMs"/> (general barrel time, timed
    /// from absolute simulation start) - confirmed live to differ by ~0.07 ms on a real load.</summary>
    public double? BulletLeadTime10PmaxMs { get; init; }
}

/// <summary>One point of GRT's own pressure/velocity/time curve for one simulated shot - read from
/// the chunk stream Get_TabResults opens but (via <see cref="GrtClient.GetTabResultsAsync"/>) normally
/// closes unread. <see cref="TimeMs"/> is timed from true ignition (t=0 at the very first point) -
/// GRT's own unambiguous physical time origin, unlike "BarrelTime"/"BulletLeadTime10Pmax" which are
/// each referenced from a different, less obvious epoch (see reference_grt_obt_concept memory).</summary>
public sealed record GrtCurvePoint(
    double PositionMm, double ProjectilePositionMm, double BurnedFraction,
    double PressureBar, double VelocityMps, double EnergyJoule, double TimeMs);

/// <summary>
/// Minimal client for the GRT plugin IPC channel (plain TCP on 127.0.0.1,
/// concatenated compact-JSON messages). Commands: Get_TabOnTop, Get_TabResults, Load_File.
/// </summary>
public sealed class GrtClient : IDisposable
{
    private readonly int _port;
    private readonly string? _ipcFile;
    private readonly Socket _socket;
    private readonly JsonFrameSplitter _splitter = new();
    private readonly SemaphoreSlim _reqGate = new(1, 1);
    private readonly object _sync = new();

    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<JsonElement>? _pending;

    /// <summary>Connects to GRT on 127.0.0.1:<paramref name="port"/> (<c>--ipcport</c>, Windows and macOS).</summary>
    public GrtClient(int port)
    {
        _port = port;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    }

    /// <summary>Connects to GRT through the Unix socket file at <paramref name="socketPath"/>
    /// (<c>--ipcfile</c>, GRT's Linux transport).</summary>
    public GrtClient(string socketPath)
    {
        _ipcFile = socketPath;
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    }

    /// <summary>The TCP port; 0 when connected through a socket file.</summary>
    public int Port => _port;
    /// <summary>The Unix socket file; null when connected over TCP.</summary>
    public string? IpcFile => _ipcFile;
    public bool Connected => _socket.Connected;

    private readonly object _activationLock = new();
    private string? _pendingActivation;
    private Action<string>? _activated;

    /// <summary>Raised (background thread) for menu/toolbar clicks from GRT, with the item id
    /// (e.g. "com.grt.plugin.x.athlon"). Empty string if GRT sent no id.
    /// If GRT fires the launch click before anyone subscribes, that id is buffered and
    /// replayed to the first subscriber (see <see cref="PendingActivation"/>).</summary>
    public event Action<string>? MenuOrToolbarActivated
    {
        add
        {
            string? replay = null;
            lock (_activationLock)
            {
                _activated += value;
                if (_pendingActivation != null) { replay = _pendingActivation; _pendingActivation = null; }
            }
            if (replay != null) value?.Invoke(replay);
        }
        remove { lock (_activationLock) _activated -= value; }
    }

    /// <summary>The menu/toolbar id GRT sent before any handler was attached (the onDemand launch
    /// click), consumed once. Null once replayed or if none is buffered.</summary>
    public string? PendingActivation { get { lock (_activationLock) return _pendingActivation; } }

    /// <summary>Raised (background thread) with the GRT UI language code from Event_Attached.</summary>
    public event Action<string>? Attached;

    /// <summary>Raised (on a background thread) with a human-readable log line.</summary>
    public event Action<string>? Log;

    public void Connect()
    {
        if (_ipcFile != null) _socket.Connect(new UnixDomainSocketEndPoint(_ipcFile));
        else _socket.Connect(System.Net.IPAddress.Loopback, _port);
        _stream = new NetworkStream(_socket, ownsSocket: false);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        Log?.Invoke(_ipcFile != null ? $"connected to GRT on {_ipcFile}" : $"connected to GRT on 127.0.0.1:{_port}");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                string text = Encoding.UTF8.GetString(buffer, 0, n);
                foreach (string msg in _splitter.Feed(text))
                    Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke("read loop stopped: " + ex.Message); }
    }

    private void Dispatch(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { Log?.Invoke("bad JSON from GRT: " + ex.Message); return; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var first = doc.RootElement.EnumerateObject().FirstOrDefault();
            string name = first.Name;

            if (name == "Result")
            {
                Log?.Invoke("RCV: " + Trim(json));
                lock (_sync)
                {
                    var p = _pending;
                    _pending = null;
                    p?.TrySetResult(first.Value.Clone());
                }
                return;
            }

            if (name.StartsWith("Event_", StringComparison.Ordinal))
            {
                Log?.Invoke("EVT: " + name);
                if (name is "Event_MenuAction" or "Event_ToolbarAction")
                {
                    string id = first.Value.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    Action<string>? h;
                    lock (_activationLock)
                    {
                        h = _activated;
                        if (h == null) { _pendingActivation = id; Log?.Invoke($"buffered activation '{id}' (no handler yet)"); }
                    }
                    h?.Invoke(id);
                }
                else if (name == "Event_Attached")
                {
                    string lc = first.Value.TryGetProperty("LanguageCode", out var l) ? l.GetString() ?? ""
                              : first.Value.TryGetProperty("languagecode", out var l2) ? l2.GetString() ?? "" : "";
                    Attached?.Invoke(lc);
                }
            }
        }
    }

    private async Task<JsonElement> RequestAsync(string command, TimeSpan timeout)
    {
        if (_stream == null) throw new InvalidOperationException("not connected to GRT");

        await _reqGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _pending = tcs;

            Log?.Invoke("SND: " + command);
            byte[] bytes = Encoding.UTF8.GetBytes(command);
            await _stream.WriteAsync(bytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeout);
            await using (cts.Token.Register(() => tcs.TrySetException(new TimeoutException("GRT did not answer " + command))))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _pending = null;
            _reqGate.Release();
        }
    }

    /// <summary>Returns (tabHandle, caption, filePath) of the tab currently on top in GRT.</summary>
    public async Task<(long handle, string caption, string file)> GetTabOnTopAsync()
    {
        JsonElement result = await RequestAsync("{\"Get_TabOnTop\":null}", TimeSpan.FromSeconds(3));
        if (GetStatus(result) != "success")
            throw new InvalidOperationException("Get_TabOnTop failed: " + GetMessage(result));

        if (!result.TryGetProperty("values", out JsonElement v))
            throw new InvalidOperationException("Get_TabOnTop: no 'values' in response");
        Log?.Invoke("     values: " + v.GetRawText());

        return (Str(v, "tabhandle") is { } h && long.TryParse(h, out long hn) ? hn : 0,
                Str(v, "caption") ?? "",
                Str(v, "file") ?? "");
    }

    /// <summary>Reads a field as text regardless of whether GRT encoded it as a JSON string, number or bool.</summary>
    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => e.GetRawText(),
        };
    }

    /// <summary>Asks GRT to open a .grtload file in a new tab of the current window.</summary>
    public async Task LoadFileAsync(string path)
    {
        string cmd = "{\"Load_File\":{\"filepath\":" + JsonSerializer.Serialize(path) + "}}";
        JsonElement result = await RequestAsync(cmd, TimeSpan.FromSeconds(5));
        if (GetStatus(result) != "success")
            throw new InvalidOperationException("Load_File failed: " + GetMessage(result));
    }

    /// <summary>Scalar simulation results for the charge/curve currently shown in the given tab.</summary>
    public async Task<GrtResults> GetTabResultsAsync(long tabHandle)
    {
        JsonElement result = await RequestAsync(
            "{\"Get_TabResults\":{\"tabhandle\":\"" + tabHandle.ToString(CultureInfo.InvariantCulture) + "\"}}",
            TimeSpan.FromSeconds(6));
        if (GetStatus(result) != "success")
            throw new InvalidOperationException("Get_TabResults failed: " + GetMessage(result));

        JsonElement v = result.GetProperty("values");
        JsonElement d = v.TryGetProperty("data", out var dd) ? dd : v;

        // GRT opened a chunk stream for the P/V/t curve — close it, we only want scalars.
        // MUST be an awaited request (not fire-and-forget): a raw write here races with the next
        // command on the socket and GRT then rejects the garbled bytes ("bad request").
        if (Str(v, "chunkStreamHandle") is { } cs && cs != "0")
        {
            try { await RequestAsync("{\"Close_ChunkStream\":{\"chunkStreamHandle\":\"" + cs + "\"}}", TimeSpan.FromSeconds(3)); }
            catch { /* best effort */ }
        }

        return new GrtResults
        {
            MuzzleVelocityMps = ValueOf(d, "MuzzleVelocity") ?? ValueOf(d, "EndVelocity"),
            MaxPressure = ValueOf(d, "MaxPressure"),
            MaxPressureUnit = UnitOf(d, "MaxPressure"),
            // "BarrelTime"/"EndTime" never actually appear in the live IPC payload (verified against
            // a real GRT 2021.2030 response 2026-09-14) - the real field is "MuzzleTime". Kept as a
            // fallback in case a different GRT version does use one of those names.
            BarrelTimeMs = ValueOf(d, "MuzzleTime") ?? ValueOf(d, "BarrelTime") ?? ValueOf(d, "EndTime"),
            BurnRatio = ValueOf(d, "BurnRatio"),
            LoadRatio = ValueOf(d, "LoadRatio"),
            OptimalBarrelTimeMs = ValueOf(d, "OptimalBarrelTime"),
            OptimalBarrelTimeNode = Str(d, "OptimalBarrelTimeNode") ?? "",
            BulletLeadTime10PmaxMs = ValueOf(d, "BulletLeadTime10Pmax"),
        };
    }

    /// <summary>
    /// Same scalar results as <see cref="GetTabResultsAsync"/>, but also reads every chunk of the
    /// P/V/t curve before closing the stream (that method closes it unread, since it only wants the
    /// scalars - see its own comment). Reads chunks by index up to the reported <c>chunkCount</c>,
    /// stopping early if a chunk comes back with an empty <c>data</c> array or <c>EOF</c> - GRT is
    /// explicit that chunks can be read "in any order", so index order isn't required, but reading
    /// 0..n-1 in sequence is simplest and matches how every other plugin (Trajectory) does it.
    /// </summary>
    public async Task<(GrtResults Results, GrtCurvePoint[] Curve)> GetTabResultsWithCurveAsync(long tabHandle)
    {
        JsonElement result = await RequestAsync(
            "{\"Get_TabResults\":{\"tabhandle\":\"" + tabHandle.ToString(CultureInfo.InvariantCulture) + "\"}}",
            TimeSpan.FromSeconds(6));
        if (GetStatus(result) != "success")
            throw new InvalidOperationException("Get_TabResults failed: " + GetMessage(result));

        JsonElement v = result.GetProperty("values");
        JsonElement d = v.TryGetProperty("data", out var dd) ? dd : v;

        var curve = new List<GrtCurvePoint>();
        if (Str(v, "chunkStreamHandle") is { } cs && cs != "0")
        {
            int chunkCount = int.TryParse(Str(v, "chunkCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
            for (int i = 0; i < chunkCount; i++)
            {
                JsonElement chunkResult;
                try
                {
                    chunkResult = await RequestAsync(
                        "{\"Get_Chunk\":{\"chunkStreamHandle\":\"" + cs + "\",\"chunkIndex\":\"" + i.ToString(CultureInfo.InvariantCulture) + "\"}}",
                        TimeSpan.FromSeconds(5));
                }
                catch { break; }
                if (GetStatus(chunkResult) != "success") break;
                JsonElement cv = chunkResult.GetProperty("values");
                if (!cv.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
                bool any = false;
                foreach (JsonElement pt in arr.EnumerateArray())
                {
                    any = true;
                    curve.Add(new GrtCurvePoint(
                        Num(pt, "x"), Num(pt, "xp"), Num(pt, "z"), Num(pt, "p"), Num(pt, "v"), Num(pt, "e"), Num(pt, "t")));
                }
                bool eof = cv.TryGetProperty("EOF", out var eofEl) && (eofEl.ValueKind == JsonValueKind.True ||
                    (eofEl.ValueKind == JsonValueKind.String && string.Equals(eofEl.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
                if (!any || eof) break;
            }

            try { await RequestAsync("{\"Close_ChunkStream\":{\"chunkStreamHandle\":\"" + cs + "\"}}", TimeSpan.FromSeconds(3)); }
            catch { /* best effort */ }
        }

        var results = new GrtResults
        {
            MuzzleVelocityMps = ValueOf(d, "MuzzleVelocity") ?? ValueOf(d, "EndVelocity"),
            MaxPressure = ValueOf(d, "MaxPressure"),
            MaxPressureUnit = UnitOf(d, "MaxPressure"),
            BarrelTimeMs = ValueOf(d, "MuzzleTime") ?? ValueOf(d, "BarrelTime") ?? ValueOf(d, "EndTime"),
            BurnRatio = ValueOf(d, "BurnRatio"),
            LoadRatio = ValueOf(d, "LoadRatio"),
            OptimalBarrelTimeMs = ValueOf(d, "OptimalBarrelTime"),
            OptimalBarrelTimeNode = Str(d, "OptimalBarrelTimeNode") ?? "",
            BulletLeadTime10PmaxMs = ValueOf(d, "BulletLeadTime10Pmax"),
        };
        return (results, curve.ToArray());
    }

    /// <summary>Reads a JSON number field regardless of whether GRT encoded it as a raw number or a
    /// string (see the "field types are loose" note in reference_grt_plugin_ipc) - Get_Chunk's own
    /// documented example uses raw numbers, unlike most other GRT fields.</summary>
    private static double Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement e)) return 0;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0,
            _ => 0,
        };
    }

    /// <summary>"850.5 m/s" → 850.5, "2,850 fps" → 2850 (first numeric token, unit stripped).</summary>
    private static double? ValueOf(JsonElement obj, string name)
        // fully qualified: the local Str(JsonElement, string) helper hides GrtPluginKit.Util.Str.
        => GrtPluginKit.Util.Str.ParseNumber(Str(obj, name));

    private static string UnitOf(JsonElement obj, string name)
    {
        string? s = Str(obj, name);
        if (string.IsNullOrWhiteSpace(s)) return "";
        int sp = s.IndexOf(' ');
        return sp >= 0 ? s[(sp + 1)..].Trim() : "";
    }

    private static string GetStatus(JsonElement result) =>
        result.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

    private static string GetMessage(JsonElement result) =>
        result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "(no message)";

    private static string Trim(string s) => s.Length <= 2000 ? s : s[..2000] + "…";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
        _cts?.Dispose();
        _reqGate.Dispose();
    }
}
