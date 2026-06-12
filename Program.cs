using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var auth = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });

if (!string.IsNullOrEmpty(cfg["Auth:Google:ClientId"]))
    auth.AddGoogle(o =>
    {
        o.ClientId     = cfg["Auth:Google:ClientId"]!;
        o.ClientSecret = cfg["Auth:Google:ClientSecret"]!;
        o.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");
    });

if (!string.IsNullOrEmpty(cfg["Auth:Microsoft:ClientId"]))
    auth.AddMicrosoftAccount(o =>
    {
        o.ClientId     = cfg["Auth:Microsoft:ClientId"]!;
        o.ClientSecret = cfg["Auth:Microsoft:ClientSecret"]!;
    });

if (!string.IsNullOrEmpty(cfg["Auth:GitHub:ClientId"]))
    auth.AddGitHub(o =>
    {
        o.ClientId     = cfg["Auth:GitHub:ClientId"]!;
        o.ClientSecret = cfg["Auth:GitHub:ClientSecret"]!;
    });

var app = builder.Build();

var dbPath = Path.Combine(app.Environment.ContentRootPath, "results.db");
using (var conn = new SqliteConnection($"Data Source={dbPath}"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS game_results (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id   TEXT    NOT NULL DEFAULT '',
            game      TEXT    NOT NULL,
            result    TEXT    NOT NULL,
            net       INTEGER NOT NULL,
            detail    TEXT    NOT NULL DEFAULT '',
            played_at TEXT    NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS users (
            id         TEXT PRIMARY KEY,
            provider   TEXT NOT NULL,
            name       TEXT NOT NULL DEFAULT '',
            email      TEXT,
            avatar_url TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;
    cmd.ExecuteNonQuery();
    try
    {
        cmd.CommandText = "ALTER TABLE game_results ADD COLUMN user_id TEXT NOT NULL DEFAULT ''";
        cmd.ExecuteNonQuery();
    }
    catch { }
}

app.UseAuthentication();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/auth/providers", () =>
{
    var list = new List<string>();
    if (!string.IsNullOrEmpty(cfg["Auth:Google:ClientId"]))    list.Add("google");
    if (!string.IsNullOrEmpty(cfg["Auth:Microsoft:ClientId"])) list.Add("microsoft");
    if (!string.IsNullOrEmpty(cfg["Auth:GitHub:ClientId"]))    list.Add("github");
    return Results.Ok(list);
});

app.MapGet("/auth/login/{provider}", async (string provider, HttpContext ctx) =>
{
    await ctx.ChallengeAsync(provider, new AuthenticationProperties { RedirectUri = "/" });
});

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/me", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Ok(new { authenticated = false, name = (string?)null, email = (string?)null, avatar = (string?)null });

    var name   = ctx.User.Identity.Name ?? "Player";
    var email  = ctx.User.FindFirstValue(ClaimTypes.Email);
    var avatar = ctx.User.FindFirstValue("urn:google:picture")
              ?? ctx.User.FindFirstValue("urn:github:avatar_url");
    return Results.Ok(new { authenticated = true, name, email, avatar });
});

app.MapPost("/api/results", async (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var body = await ctx.Request.ReadFromJsonAsync<GameResult>();
    if (body is null) return Results.BadRequest();
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO game_results (user_id,game,result,net,detail) VALUES ($uid,$game,$result,$net,$detail)";
    cmd.Parameters.AddWithValue("$uid",    userId);
    cmd.Parameters.AddWithValue("$game",   body.Game);
    cmd.Parameters.AddWithValue("$result", body.Result);
    cmd.Parameters.AddWithValue("$net",    body.Net);
    cmd.Parameters.AddWithValue("$detail", body.Detail ?? "");
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

app.MapGet("/api/results", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    var rows = new List<object>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id,game,result,net,detail,played_at FROM game_results WHERE user_id=$uid ORDER BY id DESC LIMIT 100";
    cmd.Parameters.AddWithValue("$uid", userId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        rows.Add(new { id = reader.GetInt64(0), game = reader.GetString(1), result = reader.GetString(2), net = reader.GetInt64(3), detail = reader.GetString(4), playedAt = reader.GetString(5) });
    return Results.Ok(rows);
});

app.Run();

record GameResult(string Game, string Result, int Net, string? Detail);
