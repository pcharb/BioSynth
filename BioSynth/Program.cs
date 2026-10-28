using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using BioSynth.App;

// ─────────────────────────────────────────────────────────────────────────────
// BioSynth : application unique pour Windows, Linux et macOS
//   dotnet run                    → ouvre la fenêtre BioSynth (http://localhost:5080)
//   dotnet run -- --port 6000     → autre port
//   dotnet run -- --browser       → ouvrir dans un onglet du navigateur par défaut
//   dotnet run -- --no-browser    → n'ouvrir aucune fenêtre (machine sans écran, tunnel SSH)
//   dotnet run -- --keep-server   → laisser le serveur actif après fermeture de la fenêtre
//   dotnet run -- --lan           → écouter sur toutes les interfaces (accès depuis
//                                   une autre machine ; à éviter sur un réseau non fiable)
// ─────────────────────────────────────────────────────────────────────────────

int port = 5080;
bool openWindow = true, appMode = true, keepServer = false, lan = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p)) port = p;
    if (args[i] == "--no-browser") openWindow = false;
    if (args[i] == "--browser") appMode = false;
    if (args[i] == "--keep-server") keepServer = true;
    if (args[i] == "--lan") lan = true;
}

// Une instance tourne déjà sur ce port : on lui ouvre simplement une fenêtre.
if (AppWindow.PortInUse(port))
{
    string existing = $"http://localhost:{port}/";
    Console.WriteLine($"BioSynth semble déjà actif sur {existing} (sinon, choisir un autre port avec --port).");
    if (openWindow) AppWindow.OpenDefault(existing);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args            = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath     = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.WebHost.UseUrls(lan ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}");
builder.Services.AddSingleton<SimulatorHost>();
builder.Services.AddSingleton<Broadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broadcaster>());
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

var host = app.Services.GetRequiredService<SimulatorHost>();

// ── Métadonnées / état ──────────────────────────────────────────────────────
app.MapGet("/api/meta",  () => Results.Json(host.Meta()));
app.MapGet("/api/zones", () => Results.Json(host.ZonesState()));

// ── EEG ─────────────────────────────────────────────────────────────────────
app.MapPost("/api/eeg/start",   (EegStartRequest r)  => Results.Json(host.StartEeg(r)));
app.MapPost("/api/eeg/stop",    ()                   => Results.Json(host.StopEeg()));
app.MapPost("/api/eeg/display", (DisplayRequest r)   => Results.Json(host.SetDisplayChannels(r.Channels)));
app.MapPost("/api/eeg/inspect", (PathsRequest r)     => Results.Json(host.InspectEegFiles(r.Paths)));
app.MapPost("/api/eeg/speed",   (SpeedRequest r)     => Results.Json(host.SetReplaySpeed(r.Speed)));
app.MapPost("/api/eeg/pause",   ()                   => Results.Json(host.ToggleReplayPause()));
app.MapPost("/api/lsl/discover", () => Results.Json(host.DiscoverLsl()));
app.MapPost("/api/lsl/toggle",  (LslToggleRequest r) => Results.Json(host.ToggleLsl(r)));

// ── Zones cérébrales ────────────────────────────────────────────────────────
app.MapPost("/api/zones",        (ZonesRequest r)  => Results.Json(host.SetZones(r.Activation)));
app.MapPost("/api/zones/preset", (PresetRequest r) => Results.Json(host.ApplyPreset(r.Preset)));

// ── Eye Tracking ────────────────────────────────────────────────────────────
app.MapPost("/api/et/start",   (EtStartRequest r) => Results.Json(host.StartEt(r)));
app.MapPost("/api/et/stop",    ()                 => Results.Json(host.StopEt()));
app.MapPost("/api/et/inspect", (PathsRequest r)   => Results.Json(host.InspectEtFiles(r.Paths)));
app.MapPost("/api/et/speed",   (SpeedRequest r)   => Results.Json(host.SetEtReplaySpeed(r.Speed)));
app.MapPost("/api/et/pause",   ()                 => Results.Json(host.ToggleEtReplayPause()));

// ── Face Tracking ───────────────────────────────────────────────────────────
app.MapPost("/api/ft/start", (FtStartRequest r) => Results.Json(host.StartFt(r)));
app.MapPost("/api/ft/stop",  ()                 => Results.Json(host.StopFt()));

// ── Modèle 3D anatomique (facultatif) ───────────────────────────────────────
app.MapGet("/api/brain-model", () =>
    host.FindBrainModel() is string path
        ? Results.File(path, "text/plain; charset=utf-8")
        : Results.NotFound());

// ── Sélecteur de fichiers (remplace les boîtes de dialogue Windows) ─────────
// Le serveur tourne sur la machine de l'utilisateur : on liste son système de fichiers local.
app.MapGet("/api/fs", (string? path) =>
{
    try
    {
        string dir = string.IsNullOrWhiteSpace(path) ? host.DataDir : path;
        if (File.Exists(dir)) dir = Path.GetDirectoryName(dir) ?? host.DataDir;
        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir)) return Results.Json(new { ok = false, error = $"Dossier introuvable : {dir}" });

        var di = new DirectoryInfo(dir);
        var dirs = di.EnumerateDirectories()
                     .Where(d => !d.Name.StartsWith('.'))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(d => d.Name).ToArray();
        var files = di.EnumerateFiles()
                      .Where(f => !f.Name.StartsWith('.'))
                      .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                      .Select(f => new { name = f.Name, size = f.Length }).ToArray();
        return Results.Json(new { ok = true, path = dir, parent = di.Parent?.FullName, sep = Path.DirectorySeparatorChar.ToString(), dirs, files, places = Places(host.DataDir) });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// Emplacements rapides du sélecteur de fichiers
static List<object> Places(string dataDir)
{
    var list = new List<object>();
    void Add(string label, string? path) { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new { label, path }); }
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    Add("BioSynth", dataDir);
    Add("Dossier perso", home);
    foreach (var (label, names) in new[] { ("Documents", new[] { "Documents" }), ("Téléchargements", new[] { "Téléchargements", "Downloads" }), ("Bureau", new[] { "Bureau", "Desktop" }) })
        foreach (var n in names) if (Directory.Exists(Path.Combine(home, n))) { Add(label, Path.Combine(home, n)); break; }
    if (OperatingSystem.IsWindows())
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network))
            Add(d.Name, d.RootDirectory.FullName);
    }
    else
    {
        Add("/", "/");
        // Supports amovibles et partages montés (clés USB, disques externes, NAS)
        foreach (var root in new[] { Path.Combine("/media", Environment.UserName), "/media", "/mnt", "/run/media/" + Environment.UserName })
        {
            if (!Directory.Exists(root)) continue;
            try { foreach (var d in Directory.GetDirectories(root).Where(d => !d.EndsWith("/" + Environment.UserName)).Take(8)) Add(Path.GetFileName(d), d); } catch { }
        }
    }
    return list;
}

// ── WebSocket : trames temps réel ───────────────────────────────────────────
app.Map("/ws", async (HttpContext ctx, Broadcaster bc) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    bool topoOnly = ctx.Request.Query["kind"] == "topo";
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await bc.Serve(ws, topoOnly, ctx.RequestAborted);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    string url = $"http://localhost:{port}/";
    Console.WriteLine();
    Console.WriteLine($"  BioSynth est prêt : {url}");
    Console.WriteLine($"  liblsl : {(host.LslAvailable ? "détectée" : "introuvable (LSL désactivé)")}");
    Console.WriteLine( "  Ctrl+C pour quitter.");
    Console.WriteLine();
    if (!openWindow) return;

    var window = AppWindow.Open(url, Path.Combine(host.DataDir, ".fenetre"), appMode);
    if (window == null || keepServer) return;

    // Fermer la fenêtre BioSynth arrête l'application, comme pour un logiciel de bureau.
    // Si le navigateur rend la main immédiatement (lanceur intermédiaire), on garde le serveur.
    var started = DateTime.UtcNow;
    _ = Task.Run(async () =>
    {
        await window.WaitForExitAsync();
        if (DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
        {
            Console.WriteLine("  La fenêtre a rendu la main aussitôt : le serveur reste actif (Ctrl+C pour quitter).");
            return;
        }
        Console.WriteLine("  Fenêtre fermée : arrêt de BioSynth.");
        app.Lifetime.StopApplication();
    });
});
app.Lifetime.ApplicationStopping.Register(host.Dispose);

app.Run();

/// <summary>Diffuse ~30 trames/s aux navigateurs connectés (interface principale et fenêtres TopoMap).</summary>
public sealed class Broadcaster : BackgroundService
{
    private readonly SimulatorHost _host;
    private readonly ConcurrentDictionary<WebSocket, Client> _clients = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private byte[]? _lastTopo;

    private sealed class Client
    {
        public bool TopoOnly;
        public readonly SemaphoreSlim Send = new(1, 1);
    }

    public Broadcaster(SimulatorHost host) => _host = host;

    public async Task Serve(WebSocket ws, bool topoOnly, CancellationToken ct)
    {
        var c = new Client { TopoOnly = topoOnly };
        _clients[ws] = c;
        try
        {
            if (_lastTopo != null) await SendAsync(ws, c, ct, _lastTopo);
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            _clients.TryRemove(ws, out _);
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                var (full, topo) = _host.Tick();
                if (_clients.IsEmpty) continue;

                byte[] fullBytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "frame", frame = full }, Json);
                byte[]? topoBytes = null;
                if (topo != null)
                {
                    topoBytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "topo", topo }, Json);
                    _lastTopo = topoBytes;
                }

                foreach (var (ws, c) in _clients)
                {
                    if (ws.State != WebSocketState.Open) continue;
                    if (c.TopoOnly) { if (topoBytes != null) _ = SendAsync(ws, c, stop, topoBytes); }
                    else _ = SendAsync(ws, c, stop, topoBytes, fullBytes);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"Diffusion : {ex.Message}"); }
        }
    }

    private static async Task SendAsync(WebSocket ws, Client c, CancellationToken ct, params byte[]?[] messages)
    {
        // Un client lent saute des trames plutôt que de les accumuler
        if (!await c.Send.WaitAsync(0, ct)) return;
        try
        {
            foreach (var m in messages)
                if (m != null) await ws.SendAsync(m, WebSocketMessageType.Text, true, ct);
        }
        catch { }
        finally { c.Send.Release(); }
    }
}
