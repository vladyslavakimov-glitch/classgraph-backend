namespace ClassGraph.Server;

public sealed class ConsoleCommandHostedService(
    CommandService commandService,
    WebSocketHub webSocketHub,
    IHostApplicationLifetime lifetime,
    ILogger<ConsoleCommandHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            logger.LogInformation("Terminaleingabe ist umgeleitet; interaktive Kommandos sind deaktiviert.");
            return;
        }

        await Task.Yield();
        Console.WriteLine();
        Console.WriteLine("ClassGraph ist bereit. 'help' zeigt alle Befehle, 'exit' beendet den Server.");
        Console.Write("classgraph> ");

        while (!stoppingToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(stoppingToken);
            if (line is null)
            {
                break;
            }

            if (string.Equals(line.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
            {
                lifetime.StopApplication();
                break;
            }

            var execution = await commandService.ExecuteAsync(line, stoppingToken);
            Console.WriteLine(execution.Result.Success ? execution.Result.Message : $"Fehler: {execution.Result.Message}");
            if (execution.Snapshot is not null)
            {
                await webSocketHub.BroadcastSnapshotAsync(execution.Snapshot, stoppingToken);
            }

            Console.Write("classgraph> ");
        }
    }
}
