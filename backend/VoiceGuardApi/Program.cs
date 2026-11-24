using System.Buffers.Binary;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.ML.OnnxRuntime;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:4173"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader()));

var dbPath = Path.GetFullPath(builder.Configuration["VoiceGuard:DatabasePath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "data", "voiceguard.db"));
builder.Services.AddSingleton(new AccountStore(dbPath));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "VoiceGuard.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("accounts", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12, Window = TimeSpan.FromMinutes(1)
        }));
});

var app = builder.Build();
app.UseCors();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/cuenta")
        || context.Request.Path.StartsWithSegments("/api/historial"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapAccounts();
const string modelName = "modelo_cnn_audio.onnx";
var modelPath = Path.Combine(AppContext.BaseDirectory, modelName);
string? modelHash = null;
if (File.Exists(modelPath))
{
    using var modelStream = File.OpenRead(modelPath);
    modelHash = Convert.ToHexString(SHA256.HashData(modelStream)).ToLowerInvariant();
}
using var session = File.Exists(modelPath) ? new InferenceSession(modelPath) : null;

if (session is not null && (!session.InputMetadata.TryGetValue("input_spectrogram", out var metadata)
    || metadata.ElementType != typeof(float)
    || !metadata.Dimensions.Skip(1).SequenceEqual(new[] { MelSpectrogram.Bands, MelSpectrogram.Frames, 1 })
    || !session.OutputMetadata.ContainsKey("output")))
    throw new InvalidOperationException("El archivo ONNX no corresponde al contrato de la CNN 2D.");

app.MapGet("/api/estado", () => Results.Ok(new
{
    modeloDisponible = session is not null,
    modelo = session is null ? null : modelName,
    sha256 = modelHash
}));

app.MapPost("/api/analisis", async (HttpRequest request, AccountStore store) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Adjunta un archivo WAV." });

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest(new { error = "El formulario de audio no es válido." });
    }

    if (form.Files.Count != 1)
        return Results.BadRequest(new { error = "Adjunta exactamente un archivo WAV." });

    var file = form.Files[0];
    if (!string.Equals(Path.GetExtension(file.FileName), ".wav", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Solo se admiten archivos WAV." });
    if (file.Length > 1024 * 1024)
        return Results.BadRequest(new { error = "El archivo supera el límite de 1 MB." });

    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer, request.HttpContext.RequestAborted);
    var samples = ReadWav(buffer.ToArray());
    if (samples is null)
        return Results.BadRequest(new { error = "Se requiere audio WAV PCM de 16 bits, mono o estéreo, de al menos un segundo." });
    if (session is null)
        return Results.Json(new { error = "El modelo de voz no está disponible." }, statusCode: 503);

    try
    {
        var input = NamedOnnxValue.CreateFromTensor("input_spectrogram",
            MelSpectrogram.Transform(samples));
        using var results = session.Run(new[] { input });
        var probability = results.Single(value => value.Name == "output").AsEnumerable<float>().Single();
        if (!float.IsFinite(probability) || probability is < 0 or > 1)
            return Results.Json(new { error = "El modelo devolvió un resultado inválido." }, statusCode: 503);

        var label = probability >= 0.5 ? "sintetica" : "real";
        var confidence = Math.Round(Math.Max(probability, 1 - probability), 2);
        if (AccountEndpoints.UserId(request.HttpContext.User) is { } userId)
            store.Add(userId, file.FileName, label, confidence);
        return Results.Ok(new
        {
            resultado = label,
            confianza = confidence,
            mensaje = "Análisis ejecutado mediante OnnxRuntime en C#", modelo = modelName
        });
    }
    catch (OnnxRuntimeException ex)
    {
        app.Logger.LogError(ex, "Falló la inferencia ONNX.");
        return Results.Json(new { error = "No se pudo ejecutar el modelo." }, statusCode: 503);
    }
});

app.Run(app.Configuration["urls"] ?? "http://localhost:5000");

static float[]? ReadWav(byte[] bytes)
{
    var wav = bytes.AsSpan();
    if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        return null;

    ReadOnlySpan<byte> format = default;
    ReadOnlySpan<byte> data = default;
    for (int offset = 12; offset <= wav.Length - 8;)
    {
        var id = wav.Slice(offset, 4);
        int size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);
        offset += 8;
        if (size < 0 || size > wav.Length - offset)
            return null;
        if (id.SequenceEqual("fmt "u8))
            format = wav.Slice(offset, size);
        if (id.SequenceEqual("data"u8))
            data = wav.Slice(offset, size);
        offset += size + (size & 1);
    }

    if (format.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(format) != 1
        || BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != 16)
        return null;

    int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
    int rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
    if (channels is < 1 or > 2 || rate is < 8000 or > 96000 || data.Length < rate * channels * 2)
        return null;

    var samples = new float[MelSpectrogram.SampleCount];
    int availableFrames = data.Length / (channels * 2);
    for (int index = 0; index < samples.Length; index++)
    {
        double position = (double)index * rate / MelSpectrogram.SampleRate;
        int frame = (int)position;
        if (frame >= availableFrames) break;
        var first = ReadFrame(data, frame, channels);
        var next = ReadFrame(data, Math.Min(frame + 1, availableFrames - 1), channels);
        samples[index] = first + (next - first) * (float)(position - frame);
    }

    return samples;
}

static float ReadFrame(ReadOnlySpan<byte> data, int frame, int channels)
{
    int sum = 0;
    for (int channel = 0; channel < channels; channel++)
        sum += BinaryPrimitives.ReadInt16LittleEndian(data[((frame * channels + channel) * 2)..]);
    return sum / (channels * 32768f);
}
