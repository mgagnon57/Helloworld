using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Path.Combine(app.Environment.ContentRootPath, "results.db");
using (var conn = new SqliteConnection($"Data Source={dbPath}"))
{
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS game_results (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            game      TEXT    NOT NULL,
            result    TEXT    NOT NULL,
            net       INTEGER NOT NULL,
            detail    TEXT    NOT NULL DEFAULT '',
            played_at TEXT    NOT NULL DEFAULT (datetime('now'))
        )
        """;
    cmd.ExecuteNonQuery();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS player_state (
            id    INTEGER PRIMARY KEY CHECK (id = 1),
            chips INTEGER NOT NULL DEFAULT 500
        )
        """;
    cmd.ExecuteNonQuery();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS players (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            username   TEXT    UNIQUE NOT NULL COLLATE NOCASE,
            chips      INTEGER NOT NULL DEFAULT 500,
            created_at TEXT    NOT NULL DEFAULT (datetime('now'))
        )
        """;
    cmd.ExecuteNonQuery();
    try
    {
        cmd.CommandText = "ALTER TABLE game_results ADD COLUMN player_id INTEGER REFERENCES players(id)";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException ex) when (ex.Message.Contains("duplicate column name")) { }
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/results", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<GameResult>();
    if (body is null) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO game_results (game, result, net, detail, player_id) VALUES ($game, $result, $net, $detail, $pid)";
    cmd.Parameters.AddWithValue("$game", body.Game);
    cmd.Parameters.AddWithValue("$result", body.Result);
    cmd.Parameters.AddWithValue("$net", body.Net);
    cmd.Parameters.AddWithValue("$detail", body.Detail ?? "");
    cmd.Parameters.AddWithValue("$pid", body.PlayerId.HasValue ? (object)body.PlayerId.Value : DBNull.Value);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

app.MapGet("/api/results", () =>
{
    var rows = new List<object>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, game, result, net, detail, played_at FROM game_results ORDER BY id DESC LIMIT 100";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        rows.Add(new { id = reader.GetInt64(0), game = reader.GetString(1), result = reader.GetString(2), net = reader.GetInt64(3), detail = reader.GetString(4), playedAt = reader.GetString(5) });
    return Results.Ok(rows);
});

app.MapGet("/api/state", () =>
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO player_state (id, chips) VALUES (1, 500)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT chips FROM player_state WHERE id = 1";
    var chips = cmd.ExecuteScalar() is long v ? v : 500L;
    return Results.Ok(new { chips });
});

app.MapPut("/api/state/chips", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ChipState>();
    if (body is null) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    if (body.PlayerId.HasValue)
    {
        cmd.CommandText = "UPDATE players SET chips = $chips WHERE id = $id";
        cmd.Parameters.AddWithValue("$chips", body.Chips);
        cmd.Parameters.AddWithValue("$id", body.PlayerId.Value);
    }
    else
    {
        cmd.CommandText = "INSERT OR REPLACE INTO player_state (id, chips) VALUES (1, $chips)";
        cmd.Parameters.AddWithValue("$chips", body.Chips);
    }
    var affected = cmd.ExecuteNonQuery();
    if (body.PlayerId.HasValue && affected == 0) return Results.NotFound();
    return Results.Ok();
});

app.MapPost("/api/players/login", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PlayerLogin>();
    if (body is null || string.IsNullOrWhiteSpace(body.Username)) return Results.BadRequest();
    var username = body.Username.Trim();
    if (username.Length > 50) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = "INSERT OR IGNORE INTO players (username) VALUES ($u)";
    insertCmd.Parameters.AddWithValue("$u", username);
    insertCmd.ExecuteNonQuery();
    var selectCmd = conn.CreateCommand();
    selectCmd.CommandText = "SELECT id, username, chips FROM players WHERE username = $u";
    selectCmd.Parameters.AddWithValue("$u", username);
    using var r = selectCmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound();
    return Results.Ok(new { playerId = r.GetInt64(0), username = r.GetString(1), chips = r.GetInt64(2) });
});

app.MapGet("/api/players/{id}/history", (long id) =>
{
    var rows = new List<object>();
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var existCmd = conn.CreateCommand();
    existCmd.CommandText = "SELECT COUNT(*) FROM players WHERE id = $id";
    existCmd.Parameters.AddWithValue("$id", id);
    if (existCmd.ExecuteScalar() is not long count || count == 0) return Results.NotFound();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT id, game, result, net, detail, played_at
        FROM game_results
        WHERE player_id = $id
        ORDER BY id DESC LIMIT 100
        """;
    cmd.Parameters.AddWithValue("$id", id);
    using var r = cmd.ExecuteReader();
    while (r.Read())
        rows.Add(new {
            id       = r.GetInt64(0),
            game     = r.GetString(1),
            result   = r.GetString(2),
            net      = r.GetInt64(3),
            detail   = r.GetString(4),
            playedAt = r.GetString(5)
        });
    return Results.Ok(rows);
});

app.MapGet("/api/players/{id}/stats", (long id) =>
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var existCmd = conn.CreateCommand();
    existCmd.CommandText = "SELECT COUNT(*) FROM players WHERE id = $id";
    existCmd.Parameters.AddWithValue("$id", id);
    if (existCmd.ExecuteScalar() is not long count || count == 0) return Results.NotFound();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN result='win'  THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN result='lose' THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN result='push' THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(net), 0)
        FROM game_results WHERE player_id = $id
        """;
    cmd.Parameters.AddWithValue("$id", id);
    long total, wins, losses, pushes, net;
    using (var r = cmd.ExecuteReader())
    {
        r.Read();
        total  = r.GetInt64(0);
        wins   = r.GetInt64(1);
        losses = r.GetInt64(2);
        pushes = r.GetInt64(3);
        net    = r.GetInt64(4);
    }
    var byGameCmd = conn.CreateCommand();
    byGameCmd.CommandText = """
        SELECT game, COUNT(*),
               COALESCE(SUM(CASE WHEN result='win' THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(net), 0)
        FROM game_results WHERE player_id = $id
        GROUP BY game ORDER BY game
        """;
    byGameCmd.Parameters.AddWithValue("$id", id);
    var byGame = new List<object>();
    using (var r2 = byGameCmd.ExecuteReader())
    {
        while (r2.Read())
            byGame.Add(new {
                game   = r2.GetString(0),
                played = r2.GetInt64(1),
                wins   = r2.GetInt64(2),
                net    = r2.GetInt64(3)
            });
    }
    return Results.Ok(new { gamesPlayed = total, wins, losses, pushes, netProfit = net, byGame });
});

app.Run();

record GameResult(string Game, string Result, int Net, string? Detail, long? PlayerId);
record ChipState(long Chips, long? PlayerId);
record PlayerLogin(string Username);
