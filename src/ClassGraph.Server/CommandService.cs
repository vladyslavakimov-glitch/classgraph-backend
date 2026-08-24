using System.Text.RegularExpressions;

namespace ClassGraph.Server;

public sealed record CommandExecution(CommandResult Result, GraphSnapshotDto? Snapshot);

public sealed partial class CommandService(ObjectRegistry registry, ReflectionCatalog catalog)
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public async Task<CommandExecution> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var result = ExecuteCore(command?.Trim() ?? string.Empty);
            GraphSnapshotDto? snapshot = null;
            if (result.Success && result.Mutated)
            {
                registry.AdvanceRevision();
                snapshot = registry.BuildSnapshot();
            }

            return new CommandExecution(result, snapshot);
        }
        catch (CommandException exception)
        {
            return new CommandExecution(new CommandResult(false, exception.Message), null);
        }
        catch (Exception exception)
        {
            return new CommandExecution(
                new CommandResult(false, $"Der Befehl ist unerwartet fehlgeschlagen: {exception.Message}"),
                null);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private CommandResult ExecuteCore(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new CommandException("Bitte einen Befehl eingeben. Mit 'help' wird die Hilfe angezeigt.");
        }

        if (string.Equals(command, "help", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult(true, HelpText);
        }

        var typesMatch = TypesRegex().Match(command);
        if (typesMatch.Success)
        {
            var filter = typesMatch.Groups[1].Value.Trim();
            var names = catalog.RuntimeTypes
                .Select(type => type.FullName ?? type.Name)
                .Where(name => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new CommandResult(true, names.Length == 0 ? "Keine passenden Typen gefunden." : string.Join(Environment.NewLine, names), Data: names);
        }

        if (string.Equals(command, "objects", StringComparison.OrdinalIgnoreCase))
        {
            var objects = registry.ListObjects();
            return new CommandResult(true, objects.Count == 0 ? "Noch keine Objekte vorhanden." : string.Join(Environment.NewLine, objects), Data: objects);
        }

        var create = CreateRegex().Match(command);
        if (create.Success)
        {
            registry.Create(create.Groups[1].Value, create.Groups[2].Value);
            return new CommandResult(true, $"Objekt '{create.Groups[2].Value}' wurde als '{create.Groups[1].Value}' erzeugt.", true);
        }

        var set = SetRegex().Match(command);
        if (set.Success)
        {
            registry.SetValue(set.Groups[1].Value, set.Groups[2].Value, set.Groups[3].Value);
            return new CommandResult(true, $"'{set.Groups[1].Value}.{set.Groups[2].Value}' wurde aktualisiert.", true);
        }

        var link = LinkRegex().Match(command);
        if (link.Success)
        {
            registry.Link(link.Groups[1].Value, link.Groups[2].Value, link.Groups[3].Value);
            return new CommandResult(true, $"'{link.Groups[1].Value}.{link.Groups[2].Value}' wurde mit '{link.Groups[3].Value}' verbunden.", true);
        }

        var unlink = UnlinkRegex().Match(command);
        if (unlink.Success)
        {
            registry.Unlink(unlink.Groups[1].Value, unlink.Groups[2].Value, unlink.Groups[3].Value);
            return new CommandResult(true, $"Die Verbindung zu '{unlink.Groups[3].Value}' wurde aus '{unlink.Groups[1].Value}.{unlink.Groups[2].Value}' entfernt.", true);
        }

        var delete = DeleteRegex().Match(command);
        if (delete.Success)
        {
            registry.Delete(delete.Groups[1].Value);
            return new CommandResult(true, $"Objekt '{delete.Groups[1].Value}' wurde gelöscht.", true);
        }

        if (string.Equals(command, "reset", StringComparison.OrdinalIgnoreCase))
        {
            var count = registry.Reset();
            return new CommandResult(true, $"Objektgraph wurde geleert ({count} Objekt(e)).", true);
        }

        throw new CommandException("Unbekannter Befehl oder ungültige Syntax. Mit 'help' wird die Befehlssprache angezeigt.");
    }

    public const string HelpText = """
        Verfügbare Befehle:
          help
          types [suchtext]
          objects
          create <Typname> as <objekt-id>
          set <objekt-id>.<Property> = <Wert>
          link <quell-id>.<Property> -> <ziel-id>
          unlink <quell-id>.<Property> -> <ziel-id>
          delete <objekt-id>
          reset

        Beispiel:
          create Employee as anna
          set anna.Name = "Anna"
          create Department as entwicklung
          link entwicklung.Employees -> anna
        """;

    [GeneratedRegex(@"^types(?:\s+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypesRegex();

    [GeneratedRegex(@"^create\s+(\S+)\s+as\s+([\p{L}][\p{L}\p{N}_-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateRegex();

    [GeneratedRegex(@"^set\s+([\p{L}][\p{L}\p{N}_-]*)\.([\p{L}_][\p{L}\p{N}_]*)\s*=\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetRegex();

    [GeneratedRegex(@"^link\s+([\p{L}][\p{L}\p{N}_-]*)\.([\p{L}_][\p{L}\p{N}_]*)\s*->\s*([\p{L}][\p{L}\p{N}_-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^unlink\s+([\p{L}][\p{L}\p{N}_-]*)\.([\p{L}_][\p{L}\p{N}_]*)\s*->\s*([\p{L}][\p{L}\p{N}_-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnlinkRegex();

    [GeneratedRegex(@"^delete\s+([\p{L}][\p{L}\p{N}_-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteRegex();
}
