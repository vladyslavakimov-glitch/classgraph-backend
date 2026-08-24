using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ClassGraph.Server;

var builder = WebApplication.CreateBuilder(args);
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}
var proxyKey = builder.Configuration["ClassGraph:ProxyKey"];
var allowedOrigin = builder.Configuration["ClassGraph:AllowedOrigin"];

var configuredAssemblyPath = builder.Configuration["assembly"] ?? builder.Configuration["Analysis:AssemblyPath"];
AnalysisAssembly analysisAssembly;
try
{
    analysisAssembly = AnalysisAssembly.Load(configuredAssemblyPath);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ClassGraph konnte nicht gestartet werden: {exception.Message}");
    Environment.ExitCode = 1;
    return;
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddSingleton(analysisAssembly);
builder.Services.AddSingleton<ReflectionCatalog>();
builder.Services.AddSingleton<ObjectRegistry>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<WebSocketHub>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<ConsoleCommandHostedService>();
}

var app = builder.Build();
var webSocketOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) };
if (string.IsNullOrWhiteSpace(proxyKey))
{
    webSocketOptions.AllowedOrigins.Add("http://localhost:4200");
    webSocketOptions.AllowedOrigins.Add("http://127.0.0.1:4200");
    webSocketOptions.AllowedOrigins.Add("http://localhost:5080");
    webSocketOptions.AllowedOrigins.Add("http://127.0.0.1:5080");
}
else if (!string.IsNullOrWhiteSpace(allowedOrigin))
{
    webSocketOptions.AllowedOrigins.Add(allowedOrigin);
}
app.UseWebSockets(webSocketOptions);

app.MapGet("/", (ReflectionCatalog catalog) => Results.Ok(new
{
    name = "ClassGraph Server",
    assembly = catalog.AnalysisAssembly.Assembly.GetName().Name,
    webSocket = "/ws"
}));
app.MapGet("/health", (ReflectionCatalog catalog) => Results.Ok(new
{
    status = "ready",
    assembly = catalog.AnalysisAssembly.Assembly.GetName().Name,
    typeCount = catalog.Types.Count
}));
app.Map("/ws", async (HttpContext context, WebSocketHub hub) =>
{
    if (!string.IsNullOrWhiteSpace(proxyKey))
    {
        var suppliedKey = context.Request.Headers["X-ClassGraph-Proxy-Key"].ToString();
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(proxyKey);
        var headerIsValid = suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        var tokenIsValid = IsValidAccessToken(context.Request.Query["access_token"].ToString(), proxyKey);
        if (!headerIsValid && !tokenIsValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Die WebSocket-Verbindung ist nicht autorisiert.");
            return;
        }
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Dieser Endpunkt erwartet eine WebSocket-Verbindung.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket, context.RequestAborted);
});

app.Run();

static bool IsValidAccessToken(string token, string key)
{
    var separator = token.IndexOf('.');
    if (separator <= 0 || separator == token.Length - 1 || !long.TryParse(token[..separator], out var expires))
    {
        return false;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (expires < now - 30 || expires > now + 10 * 60)
    {
        return false;
    }

    byte[] suppliedSignature;
    try
    {
        suppliedSignature = Convert.FromHexString(token[(separator + 1)..]);
    }
    catch (FormatException)
    {
        return false;
    }

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
    var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(expires.ToString()));
    return suppliedSignature.Length == expectedSignature.Length &&
           CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature);
}

public partial class Program;
