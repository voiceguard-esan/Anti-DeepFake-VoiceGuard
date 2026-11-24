using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

internal sealed record Account(long Id, string Email, string PasswordHash);
internal sealed record Credentials(string? Email, string? Password);
internal sealed record AnalysisRecord(long Id, string Archivo, string Fecha, string Resultado, double Confianza);

internal sealed class AccountStore
{
    private readonly string connectionString;

    public AccountStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true
        }.ToString();

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS analyses (
                id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),
                filename TEXT NOT NULL, created TEXT NOT NULL, label TEXT NOT NULL, confidence REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analyses_user ON analyses(user_id, id DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public long? Register(string email, string hash)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO users(email, password_hash) VALUES ($email, $hash) ON CONFLICT(email) DO NOTHING RETURNING id";
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteScalar() as long?;
    }

    public Account? Find(string email)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, email, password_hash FROM users WHERE email = $email";
        cmd.Parameters.AddWithValue("$email", email);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new Account(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public void Add(long user, string filename, string label, double confidence)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analyses(user_id, filename, created, label, confidence)
            VALUES ($user, $file, $time, $label, $confidence);
            DELETE FROM analyses WHERE user_id = $user AND id NOT IN (
                SELECT id FROM analyses WHERE user_id = $user ORDER BY id DESC LIMIT 100
            );
            """;
        using var transaction = db.BeginTransaction();
        cmd.Transaction = transaction;
        cmd.Parameters.AddWithValue("$user", user);
        var basename = Path.GetFileName(filename);
        cmd.Parameters.AddWithValue("$file", basename[..Math.Min(basename.Length, 255)]);
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$confidence", confidence);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public List<AnalysisRecord> List(long user)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, filename, created, label, confidence FROM analyses WHERE user_id = $user ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$user", user);
        using var reader = cmd.ExecuteReader();
        var result = new List<AnalysisRecord>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4)));
        return result;
    }

    public void Clear(long user)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM analyses WHERE user_id = $user";
        cmd.Parameters.AddWithValue("$user", user);
        cmd.ExecuteNonQuery();
    }
}

internal static class AccountEndpoints
{
    public static long? UserId(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static void MapAccounts(this WebApplication app)
    {
        var auth = app.MapGroup("/api/cuenta");
        var hasher = new PasswordHasher<string>();
        var dummyHash = hasher.HashPassword(string.Empty, Guid.NewGuid().ToString());

        auth.MapPost("/registro", async (Credentials data, AccountStore store, HttpContext context) =>
        {
            var email = Normalize(data.Email);
            if (email is null || data.Password is not { Length: >= 12 and <= 128 })
                return Results.BadRequest(new { error = "Usa un correo válido y una contraseña de 12 a 128 caracteres." });
            var id = store.Register(email, hasher.HashPassword(email, data.Password));
            if (id is null)
                return Results.Conflict(new { error = "Ya existe una cuenta con ese correo." });
            await SignIn(context, id.Value, email);
            return Results.Ok(new { email });
        }).RequireRateLimiting("accounts");

        auth.MapPost("/sesion", async (Credentials data, AccountStore store, HttpContext context) =>
        {
            var email = Normalize(data.Email);
            if (email is null || data.Password is not { Length: >= 12 and <= 128 })
                return Results.Json(new { error = "Credenciales inválidas." }, statusCode: 401);
            var account = store.Find(email);
            var result = hasher.VerifyHashedPassword(email, account?.PasswordHash ?? dummyHash, data.Password);
            if (account is null || result == PasswordVerificationResult.Failed)
                return Results.Json(new { error = "Credenciales inválidas." }, statusCode: 401);
            await SignIn(context, account.Id, account.Email);
            return Results.Ok(new { email });
        }).RequireRateLimiting("accounts");

        auth.MapGet("/sesion", (HttpContext context) => Results.Ok(new { email = context.User.Identity!.Name }))
            .RequireAuthorization();
        auth.MapPost("/salir", async (HttpContext context) =>
        {
            await context.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api/historial", (HttpContext context, AccountStore store) =>
            Results.Ok(store.List(UserId(context.User)!.Value))).RequireAuthorization();
        app.MapDelete("/api/historial", (HttpContext context, AccountStore store) =>
        {
            store.Clear(UserId(context.User)!.Value);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string? Normalize(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        return email is { Length: <= 254 } && new EmailAddressAttribute().IsValid(email) ? email : null;
    }

    private static Task SignIn(HttpContext context, long id, string email) =>
        context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, email)],
            CookieAuthenticationDefaults.AuthenticationScheme)));
}
