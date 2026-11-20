using System.Security.Claims;
using System.Text.Json;
using AspNet.Security.OpenId.Steam;
using Factions.Api;
using Factions.Api.Services;
using Factions.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

// ---- Directory resolution ----------------------------------------------------
// TWO directories, and on a shared-data deployment they are not the same one.
//
//   instance dir — a Torch root's own Instance\Factions. Holds tuning.json and the bridge.
//   data dir     — where state lives: brains, index, events, caches. Several Torch roots can
//                  share one, so this is not necessarily any root's own instance folder.
//
// The data dir is DERIVED, never separately configured, because tuning.json is the file that
// names it (FactionTuning.DataDirectory) and the plugin resolves it through the same Core helper.
// Configuring the dashboard's copy independently is how it comes to show a different world from
// the server it is pointed at: found live 2026-08-26 with the service reading a root's instance
// folder that held eleven-hour-old decoys and no index.json at all, reporting an empty world.
//
// Priority for the INSTANCE dir: FACTIONS_INSTANCE_DIR > --instance-dir > FACTIONS_DATA_DIR >
// --data-dir > appsettings InstanceDir > appsettings DataDir > the historical default. The
// data-dir spellings are kept as aliases so an existing service command line
// ("--data-dir <root>\Instance\Factions", which is what build.ps1 writes) keeps working unchanged
// and simply gains the redirect.
static string? Arg(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

// ---- Process log ---------------------------------------------------------------
// Registered before anything else can throw, so a fault during startup is recorded too.
var fileLog = new ApiFileLog(Path.Combine(AppContext.BaseDirectory, "logs"));
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    fileLog.Error($"API_UNHANDLED terminating={e.IsTerminating} {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
    fileLog.Warn($"API_UNOBSERVED_TASK {e.Exception}");
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    fileLog.Info($"API_PROCESS_EXIT pid={Environment.ProcessId} exitCode={Environment.ExitCode}");

string? instanceDir = Environment.GetEnvironmentVariable("FACTIONS_INSTANCE_DIR")
                      ?? Arg(args, "--instance-dir")
                      ?? Environment.GetEnvironmentVariable("FACTIONS_DATA_DIR")
                      ?? Arg(args, "--data-dir");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService();

if (string.IsNullOrWhiteSpace(instanceDir))
    instanceDir = builder.Configuration["InstanceDir"];
if (string.IsNullOrWhiteSpace(instanceDir))
    instanceDir = builder.Configuration["DataDir"];
if (string.IsNullOrWhiteSpace(instanceDir))
    instanceDir = @"C:\Torch\torch-server\Instance\Factions";

// An unreadable or absent tuning.json leaves the data dir equal to the instance dir, which is the
// single-root behaviour every deployment had before the split. It must not throw: the dashboard
// starting against a stopped server is normal.
string dataDir = instanceDir;
try
{
    string tuningPath = Path.Combine(instanceDir, "tuning.json");
    if (File.Exists(tuningPath))
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(tuningPath));
        if (doc.RootElement.TryGetProperty("DataDirectory", out JsonElement configured))
            dataDir = DataDirectory.Resolve(configured.GetString() ?? string.Empty, instanceDir);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Factions.Api] could not read tuning.json for the data directory: {ex.Message}");
}

Console.WriteLine($"[Factions.Api] instance dir: {instanceDir}");
Console.WriteLine($"[Factions.Api] data dir:     {dataDir}"
                  + (string.Equals(dataDir, instanceDir, StringComparison.OrdinalIgnoreCase)
                     ? " (same as instance)" : " (redirected by tuning.json)"));

// built= is the DLL's publish stamp: it tells the outgoing process from the incoming one when a
// deploy's stop and start land in the same second.
string builtUtc = File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, "Factions.Api.dll"))
    .ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
fileLog.Info($"API_START pid={Environment.ProcessId} built={builtUtc} " +
             $"service={Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()} " +
             $"instanceDir={instanceDir} dataDir={dataDir}");
int pruned = fileLog.Prune(keepDays: 14);
if (pruned > 0) fileLog.Info($"API_LOG_PRUNED files={pruned} keepDays=14");
builder.Logging.AddProvider(new ApiFileLoggerProvider(fileLog));

// ---- Services ----------------------------------------------------------------
builder.Services.AddSingleton(new SnapshotService(dataDir, instanceDir));
builder.Services.AddSingleton<EventTailerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventTailerService>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Steam OpenID 2.0 sign-in: Steam verifies the user and hands back a claimed_id URL ending
// in the SteamID64; the session then rides a local auth cookie. No Steam API key needed.
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = SteamAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "factions.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        // API server: answer unauthenticated requests with 401 instead of a login redirect.
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    })
    .AddSteam();

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? builder.Configuration["Urls"]
    ?? "http://0.0.0.0:7891");

// ---- App -----------------------------------------------------------------------
var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// API endpoints
app.MapGet("/api/world", (SnapshotService svc) =>
    Results.Json(svc.GetSnapshot()));

// Status carries the brain-load failures and the unread inputs as well as the directories, because
// this is the endpoint anyone reaches for first and both are invisible everywhere else -- an
// unreadable brain just makes the world smaller, and an unread input just makes a number wrong.
// `ok` is false while either is failing, so a health check can see it.
app.MapGet("/api/status", (SnapshotService svc) =>
{
    WorldSnapshotDto snap = svc.GetSnapshot();
    return Results.Json(new
    {
        ok = snap.BrainLoadFailures.Count == 0 && snap.InputLoadFailures.Count == 0,
        dataDir = svc.DataDir,
        instanceDir = svc.InstanceDir,
        factions = snap.Factions.Count,
        nodes = snap.Nodes.Count,
        brainLoadFailures = snap.BrainLoadFailures,
        inputLoadFailures = snap.InputLoadFailures,
        utc = DateTime.UtcNow,
    });
});

app.MapGet("/api/production", (SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "production.json");
    if (!File.Exists(path))
        return Results.Ok(new ProductionSnapshotDto { At = DateTime.UtcNow });
    try
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ProductionSnapshotDto>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(dto ?? new ProductionSnapshotDto { At = DateTime.UtcNow });
    }
    catch
    {
        return Results.Ok(new ProductionSnapshotDto { At = DateTime.UtcNow });
    }
});

// The demand register's only pull-based view. PROD_INPUT_DEMAND logs a row on first post and on a
// 2x move, so a row posted every cycle at a stable value goes silent and is indistinguishable from
// an absent one; counts and means taken from that log are properties of the emitter as much as of
// the register. FAC-354.
app.MapGet("/api/demand", (SnapshotService svc, string? faction, string? key) =>
{
    string path = Path.Combine(svc.DataDir, "demand.json");
    if (!File.Exists(path))
        return Results.Ok(new DemandSnapshotDto { At = DateTime.UtcNow });
    try
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<DemandSnapshotDto>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto == null) return Results.Ok(new DemandSnapshotDto { At = DateTime.UtcNow });
        if (!string.IsNullOrEmpty(faction))
            dto.Entries = dto.Entries
                .Where(e => string.Equals(e.FactionTag, faction, StringComparison.OrdinalIgnoreCase))
                .ToList();
        // Substring, not equality: the caller asks for "Ore:Stone" or just "Ore:" and gets what
        // they meant. A demand Kind is "Item:<invKey>", so an exact match would need the prefix.
        if (!string.IsNullOrEmpty(key))
            dto.Entries = dto.Entries
                .Where(e => e.Kind != null && e.Kind.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        return Results.Ok(dto);
    }
    catch
    {
        return Results.Ok(new DemandSnapshotDto { At = DateTime.UtcNow });
    }
});

app.MapGet("/api/production-history", (SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "production_history.json");
    if (!File.Exists(path))
        return Results.Ok(new ProductionHistorySnapshotDto { At = DateTime.UtcNow });
    try
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<ProductionHistorySnapshotDto>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(dto ?? new ProductionHistorySnapshotDto { At = DateTime.UtcNow });
    }
    catch
    {
        return Results.Ok(new ProductionHistorySnapshotDto { At = DateTime.UtcNow });
    }
});

app.MapGet("/api/prefabs", (SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "prefab_catalogue.json");
    if (!File.Exists(path))
        return Results.Ok(new Dictionary<string, PrefabDto>());
    try
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PrefabDto>>(
            File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(dto ?? new Dictionary<string, PrefabDto>());
    }
    catch
    {
        return Results.Ok(new Dictionary<string, PrefabDto>());
    }
});

app.MapGet("/api/factions/{tag}/finance", (string tag, SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "central_bank.json");
    if (!File.Exists(path))
    {
        return Results.Ok(new
        {
            at = DateTime.UtcNow,
            factionTag = tag,
            treasury = 0L,
            assets = 0L,
            netWorth = 0L,
            incomePerQuantum = 0L,
            expensePerQuantum = 0L,
            netPerQuantum = 0L,
            report = Array.Empty<object>(),
        });
    }

    try
    {
        var json = File.ReadAllText(path);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        var events = new List<object>();
        long treasury = 0L;
        long assets = 0L;
        long netWorth = 0L;
        long incomePerQuantum = 0L;
        long expensePerQuantum = 0L;
        long netPerQuantum = 0L;

        // Try faction-specific data from brains/<tag>.json first
        bool loadedFaction = false;
        string brainPath = Path.Combine(svc.DataDir, "brains", tag + ".json");
        if (File.Exists(brainPath))
        {
            try
            {
                var brainJson = FactionStore.ReadShared(brainPath);
                var brainRoot = System.Text.Json.JsonDocument.Parse(brainJson);
                if (brainRoot.RootElement.TryGetProperty("Treasury", out var treasuryEl))
                    treasury = treasuryEl.GetInt64();

                if (brainRoot.RootElement.TryGetProperty("Sources", out var sourcesEl))
                {
                    assets = 0L;
                    foreach (var src in sourcesEl.EnumerateArray())
                    {
                        if (src.TryGetProperty("StationBookValue", out var valueEl))
                            assets += valueEl.GetInt64();
                    }
                    loadedFaction = true;
                }
            }
            catch
            {
                // Fall through to central bank totals below
            }
        }

        // Fallback to central bank snapshot data only if brain has no Sources array
        if (!loadedFaction)
        {
            if (root.TryGetProperty("snapshot", out var snapshot))
            {
                long.TryParse(snapshot.GetProperty("currentMoneySupply").GetRawText(), out treasury);
                long.TryParse(snapshot.GetProperty("worldCollateralValue").GetRawText(), out assets);
                netWorth = treasury + assets;
            }
        }

        if (root.TryGetProperty("bankEvents", out var bankEvents))
        {
            foreach (var evt in bankEvents.EnumerateArray())
            {
                if (!evt.TryGetProperty("factionTag", out var tagEl) || !string.Equals(tagEl.GetString(), tag, StringComparison.OrdinalIgnoreCase))
                    continue;
                long amount = evt.TryGetProperty("amount", out var amountEl) ? amountEl.GetInt64() : 0L;
                string type = evt.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (amount > 0) incomePerQuantum += amount;
                else expensePerQuantum += Math.Abs(amount);
                events.Add(new
                {
                    utcNow = evt.TryGetProperty("utcNow", out var utcNow) ? utcNow.GetString() : null,
                    scope = evt.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                    type = type,
                    amount = amount,
                    treasuryAfter = evt.TryGetProperty("treasuryAfter", out var treasuryAfter) ? treasuryAfter.GetInt64() : 0L,
                    debtAfter = evt.TryGetProperty("debtAfter", out var debtAfter) ? debtAfter.GetInt64() : 0L,
                    message = evt.TryGetProperty("message", out var message) ? message.GetString() : null,
                });
            }
        }

        if (root.TryGetProperty("debtEvents", out var debtEvents))
        {
            foreach (var evt in debtEvents.EnumerateArray())
            {
                if (!evt.TryGetProperty("factionTag", out var tagEl) || !string.Equals(tagEl.GetString(), tag, StringComparison.OrdinalIgnoreCase))
                    continue;
                long amount = evt.TryGetProperty("amount", out var amountEl) ? amountEl.GetInt64() : 0L;
                if (amount > 0) incomePerQuantum += amount;
                else expensePerQuantum += Math.Abs(amount);
                events.Add(new
                {
                    utcNow = evt.TryGetProperty("utcNow", out var utcNow) ? utcNow.GetString() : null,
                    scope = evt.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                    type = evt.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null,
                    amount = amount,
                    treasuryAfter = evt.TryGetProperty("treasuryAfter", out var treasuryAfter) ? treasuryAfter.GetInt64() : 0L,
                    debtAfter = evt.TryGetProperty("debtAfter", out var debtAfter) ? debtAfter.GetInt64() : 0L,
                    message = evt.TryGetProperty("message", out var message) ? message.GetString() : null,
                });
            }
        }

        netPerQuantum = incomePerQuantum - expensePerQuantum;

        return Results.Ok(new
        {
            at = DateTime.UtcNow,
            factionTag = tag,
            treasury = treasury,
            assets = assets,
            netWorth = netWorth,
            incomePerQuantum = incomePerQuantum,
            expensePerQuantum = expensePerQuantum,
            netPerQuantum = netPerQuantum,
            report = events,
        });
    }
    catch
    {
        return Results.Ok(new
        {
            at = DateTime.UtcNow,
            factionTag = tag,
            treasury = 0L,
            assets = 0L,
            netWorth = 0L,
            incomePerQuantum = 0L,
            expensePerQuantum = 0L,
            netPerQuantum = 0L,
            report = Array.Empty<object>(),
        });
    }
});

// Served straight from the file the host writes, without a typed round-trip. perf.json is
// already camelCase (every member of the anonymous objects FactionRuntime.WritePerfSnapshot
// builds is lowercase-initial) and this endpoint neither transforms, filters nor merges
// anything, so a Dto buys nothing here -- while a Dto that drifts behind the writer silently
// drops fields rather than merely mangling them. PerfSnapshotDto is retained below purely as
// the well-known empty shape for the missing-file and unreadable-file cases.
app.MapGet("/api/perf", (SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "perf.json");
    try
    {
        if (File.Exists(path))
            return Results.Text(File.ReadAllText(path), "application/json");
    }
    catch { /* fall through to the empty shape */ }
    return Results.Ok(new PerfSnapshotDto { At = DateTime.UtcNow });
});

app.MapPost("/api/command", async (SnapshotService svc, CommandRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Command))
        return Results.BadRequest(new { error = "command is empty" });
    // The bridge is per-root, so it hangs off InstanceDir, not the shared data directory.
    string bridgeDir = Path.Combine(svc.InstanceDir, "bridge");
    string inbox = Path.Combine(bridgeDir, "inbox.txt");

    // Deliberately NOT Directory.CreateDirectory. The bridge folder is created by the plugin at
    // boot; if it is missing, this API is pointed somewhere no server is reading, and creating it
    // would queue commands into a folder nothing polls -- accepted, acknowledged, never run.
    if (!Directory.Exists(bridgeDir))
        return Results.Problem(
            $"No bridge at '{bridgeDir}'. The instance directory does not belong to a running "
            + "server, so this command would never be read. Set InstanceDir to the Torch root's "
            + "Instance\\Factions folder.",
            statusCode: 503);

    await File.AppendAllTextAsync(inbox, req.Command.Trim() + "\n");
    return Results.Ok(new { queued = req.Command.Trim() });
});

// WebSocket live event stream
app.MapGet("/ws/live", async (HttpContext ctx, EventTailerService tailer) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await tailer.ServeClientAsync(ws, ctx.RequestAborted);
});

app.MapGet("/api/factions/{tag}/log", (string tag, SnapshotService svc, int lines = 200) =>
{
    string logsDir = Path.Combine(svc.DataDir, "logs");
    string needle = " " + tag + " ";
    var all = new List<string>();

    // Collect yesterday then today so the list is chronological
    for (int daysBack = 1; daysBack >= 0; daysBack--)
    {
        string date = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd");
        string path = Path.Combine(logsDir, $"Factions-{date}.log");
        if (!File.Exists(path)) continue;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Contains(needle))
                    all.Add(line);
        }
    }

    int skip = Math.Max(0, all.Count - lines);
    return Results.Json(all.GetRange(skip, all.Count - skip));
});

// ---- Steam authentication ------------------------------------------------------
app.MapGet("/auth/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/" },
    new[] { SteamAuthenticationDefaults.AuthenticationScheme }));

app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapGet("/auth/me", (HttpContext ctx, SnapshotService svc, IConfiguration cfg) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Json(new { authenticated = false });

    // The Steam claimed_id is "https://steamcommunity.com/openid/id/<steamId64>".
    string claimed = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    string steamId = claimed[(claimed.LastIndexOf('/') + 1)..];

    string? name = null;
    string? promoteLevel = null;
    string playersPath = Path.Combine(svc.DataDir, "players.json");
    if (File.Exists(playersPath))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(playersPath));
            if (doc.RootElement.TryGetProperty("Known", out var known) &&
                known.TryGetProperty(steamId, out var entry))
            {
                if (entry.TryGetProperty("Name", out var nameEl)) name = nameEl.GetString();
                if (entry.TryGetProperty("PromoteLevel", out var promoteEl)) promoteLevel = promoteEl.GetString();
            }
        }
        catch
        {
            // A missing or mid-rewrite file just means no name resolution this request.
        }
    }

    bool isAdmin = (cfg.GetSection("AdminSteamIds").Get<string[]>() ?? Array.Empty<string>())
        .Contains(steamId);

    return Results.Json(new { authenticated = true, steamId, name, promoteLevel, isAdmin });
});

app.MapGet("/api/players", (SnapshotService svc) =>
{
    string path = Path.Combine(svc.DataDir, "players.json");
    if (!File.Exists(path))
        return Results.Content("{}", "application/json");
    try
    {
        return Results.Content(File.ReadAllText(path), "application/json");
    }
    catch
    {
        return Results.Content("{}", "application/json");
    }
});

// SPA fallback — all non-API routes return index.html
app.MapFallbackToFile("index.html");

// The stop phases bracket a crash: STOPPING without STOPPED, or STOPPED without HOST_RETURNED and
// PROCESS_EXIT, names the step the process died in even when no exception line could be written.
EventTailerService tailerForLog = app.Services.GetRequiredService<EventTailerService>();
app.Lifetime.ApplicationStarted.Register(() => fileLog.Info("API_STARTED"));
app.Lifetime.ApplicationStopping.Register(() =>
    fileLog.Info($"API_STOPPING wsClients={tailerForLog.ClientCount}"));
app.Lifetime.ApplicationStopped.Register(() => fileLog.Info("API_STOPPED"));

try
{
    app.Run();
    fileLog.Info("API_HOST_RETURNED");
}
catch (Exception ex)
{
    fileLog.Error($"API_HOST_FAILED {ex}");
    throw;
}
