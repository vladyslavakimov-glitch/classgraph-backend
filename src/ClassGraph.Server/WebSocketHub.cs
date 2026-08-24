using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassGraph.Server;

public sealed class WebSocketHub(
    ReflectionCatalog catalog,
    ObjectRegistry registry,
    CommandService commandService,
    ILogger<WebSocketHub> logger)
{
    public const int MaxMessageBytes = 64 * 1024;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var client = new ClientConnection(socket);
        _clients[id] = client;

        try
        {
            var assembly = catalog.AnalysisAssembly;
            var hello = new ServerHelloDto(
                typeof(WebSocketHub).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                assembly.Assembly.GetName().Name ?? "Unbekannt",
                assembly.DisplayPath);
            await SendAsync(client, Envelope("server.hello", hello), cancellationToken);
            await SendAsync(client, Envelope("graph.snapshot", registry.BuildSnapshot(), revision: registry.Revision), cancellationToken);
            await ReceiveLoopAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "WebSocket-Client {ClientId} wurde getrennt.", id);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Verbindung beendet", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Remote endpoint is already gone.
                }
            }
        }
    }

    public Task BroadcastSnapshotAsync(GraphSnapshotDto snapshot, CancellationToken cancellationToken = default) =>
        BroadcastAsync(Envelope("graph.snapshot", snapshot, revision: snapshot.Revision), cancellationToken);

    private async Task ReceiveLoopAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await SendErrorAsync(client, null, "Nur UTF-8-JSON-Textnachrichten werden unterstützt.", cancellationToken);
                    break;
                }

                if (message.Length + result.Count > MaxMessageBytes)
                {
                    await SendErrorAsync(client, null, $"Die Nachricht überschreitet das Limit von {MaxMessageBytes / 1024} KiB.", cancellationToken);
                    await client.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Nachricht zu groß", cancellationToken);
                    return;
                }

                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
            {
                await ProcessMessageAsync(client, message.ToArray(), cancellationToken);
            }
        }
    }

    private async Task ProcessMessageAsync(ClientConnection client, byte[] utf8Json, CancellationToken cancellationToken)
    {
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            var protocolVersion = root.TryGetProperty("protocolVersion", out var versionElement) && versionElement.TryGetInt32(out var version)
                ? version
                : 0;
            requestId = root.TryGetProperty("requestId", out var requestElement) ? requestElement.GetString() : null;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            if (protocolVersion != 1)
            {
                await SendErrorAsync(client, requestId, "Nicht unterstützte Protokollversion. Erwartet wird Version 1.", cancellationToken);
                return;
            }

            switch (type)
            {
                case "snapshot.request":
                    var current = registry.BuildSnapshot();
                    await SendAsync(client, Envelope("graph.snapshot", current, requestId, current.Revision), cancellationToken);
                    break;
                case "command.execute":
                    if (!root.TryGetProperty("payload", out var payload) ||
                        !payload.TryGetProperty("text", out var textElement) ||
                        textElement.ValueKind != JsonValueKind.String)
                    {
                        await SendErrorAsync(client, requestId, "'command.execute' benötigt payload.text als String.", cancellationToken);
                        return;
                    }

                    var execution = await commandService.ExecuteAsync(textElement.GetString() ?? string.Empty, cancellationToken);
                    await SendAsync(client, Envelope("command.result", execution.Result, requestId, registry.Revision), cancellationToken);
                    if (execution.Snapshot is not null)
                    {
                        await BroadcastSnapshotAsync(execution.Snapshot, cancellationToken);
                    }
                    break;
                default:
                    await SendErrorAsync(client, requestId, $"Unbekannter Nachrichtentyp '{type ?? "<fehlt>"}'.", cancellationToken);
                    break;
            }
        }
        catch (JsonException exception)
        {
            await SendErrorAsync(client, requestId, $"Ungültiges JSON: {exception.Message}", cancellationToken);
        }
    }

    private async Task BroadcastAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        var tasks = _clients.Select(async pair =>
        {
            try
            {
                await SendAsync(pair.Value, envelope, cancellationToken);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
            {
                logger.LogDebug(exception, "Broadcast an WebSocket-Client {ClientId} fehlgeschlagen.", pair.Key);
                _clients.TryRemove(pair.Key, out var removed);
                removed?.Dispose();
            }
        });
        await Task.WhenAll(tasks);
    }

    private Task SendErrorAsync(ClientConnection client, string? requestId, string message, CancellationToken cancellationToken) =>
        SendAsync(client, Envelope("server.error", new { message }, requestId, registry.Revision), cancellationToken);

    private async Task SendAsync(ClientConnection client, ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        await client.SendGate.WaitAsync(cancellationToken);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        finally
        {
            client.SendGate.Release();
        }
    }

    private static ProtocolEnvelope Envelope(string type, object payload, string? requestId = null, long? revision = null) =>
        new(1, type, requestId, revision, payload);

    private sealed class ClientConnection(WebSocket socket) : IDisposable
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public void Dispose() => SendGate.Dispose();
    }
}
