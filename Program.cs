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
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            game     TEXT    NOT NULL,
            result   TEXT    NOT NULL,
            net      INTEGER NOT NULL,
            detail   TEXT    NOT NULL DEFAULT '',
            played_at TEXT   NOT NULL DEFAULT (datetime('now'))
        )
        """;
    cmd.ExecuteNonQuery();
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
    cmd.CommandText = "INSERT INTO game_results (game, result, net, detail) VALUES ($game, $result, $net, $detail)";
    cmd.Parameters.AddWithValue("$game", body.Game);
    cmd.Parameters.AddWithValue("$result", body.Result);
    cmd.Parameters.AddWithValue("$net", body.Net);
    cmd.Parameters.AddWithValue("$detail", body.Detail ?? "");
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

app.Run();

record GameResult(string Game, string Result, int Net, string? Detail);
