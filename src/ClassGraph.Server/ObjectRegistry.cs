using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ClassGraph.Server;

public sealed class ObjectRegistry(ReflectionCatalog catalog)
{
    private readonly Dictionary<string, RegistryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public long Revision { get; private set; }

    public void Create(string typeName, string id)
    {
        lock (_sync)
        {
            ValidateId(id);
            if (_entries.ContainsKey(id))
            {
                throw new CommandException($"Die Objekt-ID '{id}' ist bereits vergeben.");
            }

            var type = catalog.ResolveType(typeName);
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new CommandException($"Der Typ '{type.Name}' ist nicht als konkrete Klasse erzeugbar.");
            }

            var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null);
            if (constructor is null)
            {
                throw new CommandException($"Der Typ '{type.Name}' besitzt keinen öffentlichen parameterlosen Konstruktor.");
            }

            try
            {
                var instance = constructor.Invoke(null);
                _entries[id] = new RegistryEntry(id, instance, type);
            }
            catch (TargetInvocationException exception)
            {
                throw new CommandException(
                    $"Der Konstruktor von '{type.Name}' ist fehlgeschlagen: {exception.InnerException?.Message ?? exception.Message}");
            }
        }
    }

    public void SetValue(string id, string propertyName, string literal)
    {
        lock (_sync)
        {
            var entry = GetEntry(id);
            var property = GetProperty(entry.Type, propertyName);
            if (property.SetMethod?.IsPublic != true)
            {
                throw new CommandException($"Die Property '{property.Name}' von '{entry.Type.Name}' ist nicht öffentlich schreibbar.");
            }

            if (!IsSupportedScalar(property.PropertyType))
            {
                throw new CommandException(
                    $"Die Property '{property.Name}' ist kein unterstützter skalarer Wert. Für Objektreferenzen bitte 'link' verwenden.");
            }

            var value = ConvertLiteral(literal, property.PropertyType);
            try
            {
                property.SetValue(entry.Instance, value);
            }
            catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
            {
                var message = exception is TargetInvocationException invocation
                    ? invocation.InnerException?.Message ?? invocation.Message
                    : exception.Message;
                throw new CommandException($"'{entry.Id}.{property.Name}' konnte nicht gesetzt werden: {message}");
            }
        }
    }

    public void Link(string sourceId, string propertyName, string targetId)
    {
        lock (_sync)
        {
            var source = GetEntry(sourceId);
            var target = GetEntry(targetId);
            var property = GetProperty(source.Type, propertyName);

            if (TypeNameFormatter.IsCollection(property.PropertyType))
            {
                var elementType = TypeNameFormatter.GetCollectionElementType(property.PropertyType)
                    ?? throw new CommandException($"Der Elementtyp von '{property.Name}' konnte nicht bestimmt werden.");
                if (!elementType.IsAssignableFrom(target.Type))
                {
                    throw new CommandException(
                        $"'{target.Type.Name}' ist nicht mit dem Collection-Elementtyp '{elementType.Name}' kompatibel.");
                }

                var collection = ReadProperty(source, property)
                    ?? throw new CommandException($"Die Collection '{source.Id}.{property.Name}' ist nicht initialisiert.");
                var collectionInterface = FindMutableCollectionInterface(collection.GetType(), elementType)
                    ?? throw new CommandException($"Die Collection '{source.Id}.{property.Name}' ist nicht über ICollection<T> veränderbar.");

                var contains = (bool)collectionInterface.GetMethod("Contains")!.Invoke(collection, [target.Instance])!;
                if (contains)
                {
                    throw new CommandException($"'{target.Id}' ist bereits in '{source.Id}.{property.Name}' enthalten.");
                }

                collectionInterface.GetMethod("Add")!.Invoke(collection, [target.Instance]);
                return;
            }

            if (property.SetMethod?.IsPublic != true)
            {
                throw new CommandException($"Die Referenzproperty '{source.Id}.{property.Name}' ist nicht öffentlich schreibbar.");
            }

            if (!property.PropertyType.IsAssignableFrom(target.Type))
            {
                throw new CommandException(
                    $"'{target.Type.Name}' ist nicht mit '{source.Type.Name}.{property.Name}: {TypeNameFormatter.Format(property.PropertyType)}' kompatibel.");
            }

            property.SetValue(source.Instance, target.Instance);
        }
    }

    public void Unlink(string sourceId, string propertyName, string targetId)
    {
        lock (_sync)
        {
            var source = GetEntry(sourceId);
            var target = GetEntry(targetId);
            var property = GetProperty(source.Type, propertyName);

            if (TypeNameFormatter.IsCollection(property.PropertyType))
            {
                var elementType = TypeNameFormatter.GetCollectionElementType(property.PropertyType)
                    ?? throw new CommandException($"Der Elementtyp von '{property.Name}' konnte nicht bestimmt werden.");
                var collection = ReadProperty(source, property)
                    ?? throw new CommandException($"Die Collection '{source.Id}.{property.Name}' ist nicht initialisiert.");
                var collectionInterface = FindMutableCollectionInterface(collection.GetType(), elementType)
                    ?? throw new CommandException($"Die Collection '{source.Id}.{property.Name}' ist nicht veränderbar.");
                var removed = (bool)collectionInterface.GetMethod("Remove")!.Invoke(collection, [target.Instance])!;
                if (!removed)
                {
                    throw new CommandException($"'{target.Id}' ist nicht in '{source.Id}.{property.Name}' enthalten.");
                }

                return;
            }

            if (property.SetMethod?.IsPublic != true)
            {
                throw new CommandException($"Die Referenzproperty '{source.Id}.{property.Name}' ist nicht öffentlich schreibbar.");
            }

            var current = ReadProperty(source, property);
            if (!ReferenceEquals(current, target.Instance))
            {
                throw new CommandException($"'{source.Id}.{property.Name}' verweist nicht auf '{target.Id}'.");
            }

            property.SetValue(source.Instance, null);
        }
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            var target = GetEntry(id);
            foreach (var source in _entries.Values.Where(entry => !ReferenceEquals(entry.Instance, target.Instance)))
            {
                RemoveReferencesTo(source, target);
            }

            _entries.Remove(target.Id);
        }
    }

    public int Reset()
    {
        lock (_sync)
        {
            var count = _entries.Count;
            _entries.Clear();
            return count;
        }
    }

    public IReadOnlyList<string> ListObjects()
    {
        lock (_sync)
        {
            return _entries.Values
                .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.Id}: {entry.Type.FullName}")
                .ToArray();
        }
    }

    public void AdvanceRevision()
    {
        lock (_sync)
        {
            Revision++;
        }
    }

    public GraphSnapshotDto BuildSnapshot()
    {
        lock (_sync)
        {
            var byReference = _entries.Values.ToDictionary(entry => entry.Instance, entry => entry, ReferenceEqualityComparer.Instance);
            var instanceNodes = new List<InstanceNodeDto>();
            var relations = new List<InstanceRelationDto>();

            foreach (var entry in _entries.Values.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase))
            {
                var values = new List<PropertyValueDto>();
                var properties = entry.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetMethod?.IsPublic == true)
                    .OrderBy(property => property.Name, StringComparer.Ordinal);

                foreach (var property in properties)
                {
                    object? value;
                    try
                    {
                        value = property.GetValue(entry.Instance);
                    }
                    catch (Exception exception)
                    {
                        var message = exception is TargetInvocationException invocation
                            ? invocation.InnerException?.Message ?? invocation.Message
                            : exception.Message;
                        values.Add(new PropertyValueDto(property.Name, TypeNameFormatter.Format(property.PropertyType), $"<Fehler: {message}>", true));
                        continue;
                    }

                    if (value is not null && byReference.TryGetValue(value, out var referenced))
                    {
                        values.Add(new PropertyValueDto(property.Name, TypeNameFormatter.Format(property.PropertyType), $"→ {referenced.Id}"));
                        relations.Add(CreateRelation(entry, referenced, property.Name, null));
                        continue;
                    }

                    if (value is IEnumerable enumerable and not string)
                    {
                        var references = new List<string>();
                        var index = 0;
                        foreach (var item in enumerable)
                        {
                            if (item is not null && byReference.TryGetValue(item, out var collectionTarget))
                            {
                                references.Add(collectionTarget.Id);
                                relations.Add(CreateRelation(entry, collectionTarget, property.Name, index));
                            }

                            index++;
                        }

                        values.Add(new PropertyValueDto(
                            property.Name,
                            TypeNameFormatter.Format(property.PropertyType),
                            references.Count == 0 ? "[]" : $"[{string.Join(", ", references)}]"));
                        continue;
                    }

                    values.Add(new PropertyValueDto(
                        property.Name,
                        TypeNameFormatter.Format(property.PropertyType),
                        FormatValue(value)));
                }

                instanceNodes.Add(new InstanceNodeDto(entry.Id, catalog.GetTypeId(entry.Type), entry.Type.Name, values));
            }

            return new GraphSnapshotDto(Revision, catalog.Types, catalog.Relations, instanceNodes, relations);
        }
    }

    private void RemoveReferencesTo(RegistryEntry source, RegistryEntry target)
    {
        foreach (var property in source.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod?.IsPublic == true))
        {
            object? value;
            try
            {
                value = property.GetValue(source.Instance);
            }
            catch
            {
                continue;
            }

            if (ReferenceEquals(value, target.Instance) && property.SetMethod?.IsPublic == true)
            {
                property.SetValue(source.Instance, null);
                continue;
            }

            if (value is null || !TypeNameFormatter.IsCollection(property.PropertyType))
            {
                continue;
            }

            var elementType = TypeNameFormatter.GetCollectionElementType(property.PropertyType);
            if (elementType is null)
            {
                continue;
            }

            var collectionInterface = FindMutableCollectionInterface(value.GetType(), elementType);
            collectionInterface?.GetMethod("Remove")?.Invoke(value, [target.Instance]);
        }
    }

    private static object? ConvertLiteral(string rawLiteral, Type destinationType)
    {
        var targetType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        var literal = rawLiteral.Trim();

        if (string.Equals(literal, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (!destinationType.IsValueType || Nullable.GetUnderlyingType(destinationType) is not null)
            {
                return null;
            }

            throw new CommandException($"'{TypeNameFormatter.Format(destinationType)}' kann nicht null sein.");
        }

        try
        {
            if (targetType == typeof(string))
            {
                if (!(literal.StartsWith('"') && literal.EndsWith('"')))
                {
                    throw new CommandException("Strings müssen in doppelten Anführungszeichen stehen, z. B. \"Anna\".");
                }

                return JsonSerializer.Deserialize<string>(literal);
            }

            if (targetType == typeof(char))
            {
                var text = JsonSerializer.Deserialize<string>(literal);
                return text is { Length: 1 } ? text[0] : throw new FormatException("Genau ein Zeichen erwartet.");
            }

            if (targetType.IsEnum)
            {
                var enumText = literal.StartsWith('"') ? JsonSerializer.Deserialize<string>(literal) : literal;
                return Enum.Parse(targetType, enumText ?? string.Empty, ignoreCase: true);
            }

            if (targetType == typeof(Guid))
            {
                return Guid.Parse(Unquote(literal));
            }

            if (targetType == typeof(DateTime))
            {
                return DateTime.Parse(Unquote(literal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (targetType == typeof(DateOnly))
            {
                return DateOnly.Parse(Unquote(literal), CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(bool))
            {
                return bool.Parse(literal);
            }

            return Convert.ChangeType(literal, targetType, CultureInfo.InvariantCulture);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or JsonException or ArgumentException)
        {
            throw new CommandException(
                $"Der Wert '{literal}' ist für '{TypeNameFormatter.Format(destinationType)}' ungültig: {exception.Message}");
        }
    }

    private static bool IsSupportedScalar(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target.IsEnum || target.IsPrimitive || target == typeof(string) || target == typeof(decimal) ||
               target == typeof(Guid) || target == typeof(DateTime) || target == typeof(DateOnly);
    }

    private static string Unquote(string literal) => literal.StartsWith('"')
        ? JsonSerializer.Deserialize<string>(literal) ?? string.Empty
        : literal;

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => $"<{value.GetType().Name}>"
    };

    private static InstanceRelationDto CreateRelation(RegistryEntry source, RegistryEntry target, string propertyName, int? index) =>
        new($"{source.Id}:{propertyName}:{index?.ToString(CultureInfo.InvariantCulture) ?? "single"}:{target.Id}",
            source.Id,
            target.Id,
            propertyName,
            index);

    private RegistryEntry GetEntry(string id) => _entries.TryGetValue(id, out var entry)
        ? entry
        : throw new CommandException($"Das Objekt '{id}' wurde nicht gefunden.");

    private static PropertyInfo GetProperty(Type type, string name)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return properties.Length switch
        {
            0 => throw new CommandException($"Die öffentliche Property '{name}' wurde auf '{type.Name}' nicht gefunden."),
            > 1 => throw new CommandException($"Die Property '{name}' ist auf '{type.Name}' nicht eindeutig."),
            _ => properties[0]
        };
    }

    private static object? ReadProperty(RegistryEntry entry, PropertyInfo property)
    {
        if (property.GetMethod?.IsPublic != true)
        {
            throw new CommandException($"Die Property '{entry.Id}.{property.Name}' ist nicht öffentlich lesbar.");
        }

        try
        {
            return property.GetValue(entry.Instance);
        }
        catch (TargetInvocationException exception)
        {
            throw new CommandException(
                $"Die Property '{entry.Id}.{property.Name}' konnte nicht gelesen werden: {exception.InnerException?.Message ?? exception.Message}");
        }
    }

    private static Type? FindMutableCollectionInterface(Type runtimeType, Type elementType) => runtimeType.GetInterfaces()
        .Concat([runtimeType])
        .FirstOrDefault(candidate => candidate.IsGenericType &&
                                     candidate.GetGenericTypeDefinition() == typeof(ICollection<>) &&
                                     candidate.GetGenericArguments()[0].IsAssignableFrom(elementType));

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !char.IsLetter(id[0]) || id.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new CommandException("Objekt-IDs müssen mit einem Buchstaben beginnen und dürfen nur Buchstaben, Ziffern, '_' und '-' enthalten.");
        }
    }

    private sealed record RegistryEntry(string Id, object Instance, Type Type);
}
