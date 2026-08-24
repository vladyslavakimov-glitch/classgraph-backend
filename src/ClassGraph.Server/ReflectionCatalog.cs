using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClassGraph.Server;

public sealed class ReflectionCatalog
{
    private readonly Dictionary<string, Type> _typesById;
    private readonly Dictionary<Type, string> _idsByType;
    private readonly Dictionary<string, List<Type>> _typesByName;

    public ReflectionCatalog(AnalysisAssembly analysisAssembly)
    {
        AnalysisAssembly = analysisAssembly;
        var runtimeTypes = analysisAssembly.Assembly.GetExportedTypes()
            .Where(type => type.IsClass || type.IsInterface || type.IsEnum)
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        _idsByType = runtimeTypes.ToDictionary(type => type, CreateTypeId);
        _typesById = _idsByType.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        _typesByName = runtimeTypes
            .SelectMany(type => new[] { new KeyValuePair<string, Type>(type.Name, type), new(type.FullName ?? type.Name, type) })
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Value).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

        Types = runtimeTypes.Select(CreateNode).ToArray();
        Relations = CreateRelations(runtimeTypes);
    }

    public AnalysisAssembly AnalysisAssembly { get; }

    public IReadOnlyList<TypeNodeDto> Types { get; }

    public IReadOnlyList<TypeRelationDto> Relations { get; }

    public IReadOnlyCollection<Type> RuntimeTypes => _idsByType.Keys;

    public string GetTypeId(Type type) => _idsByType[type];

    public TypeNodeDto GetNode(Type type) => Types.First(node => node.Id == GetTypeId(type));

    public bool Contains(Type type) => _idsByType.ContainsKey(type);

    public Type ResolveType(string name)
    {
        if (_typesById.TryGetValue(name, out var idType))
        {
            return idType;
        }

        if (!_typesByName.TryGetValue(name.Trim(), out var candidates) || candidates.Count == 0)
        {
            throw new CommandException($"Der Typ '{name}' wurde in der geladenen Assembly nicht gefunden.");
        }

        if (candidates.Count > 1)
        {
            throw new CommandException(
                $"Der Typname '{name}' ist nicht eindeutig. Bitte Namespace verwenden: {string.Join(", ", candidates.Select(type => type.FullName))}");
        }

        return candidates[0];
    }

    private TypeNodeDto CreateNode(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(constructor => constructor.GetParameters().Length)
            .Select(constructor =>
            {
                var parameters = constructor.GetParameters()
                    .Select(parameter => new ParameterDto(
                        parameter.Name ?? "parameter",
                        TypeNameFormatter.Format(parameter.ParameterType, nullability.Create(parameter))))
                    .ToArray();
                return new ConstructorDto($"{type.Name}({FormatParameters(parameters)})", parameters);
            })
            .ToArray();

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                var info = nullability.Create(property);
                var typeName = TypeNameFormatter.Format(property.PropertyType, info);
                var accessors = $"{{ {(property.CanRead ? "get; " : string.Empty)}{(property.SetMethod?.IsPublic == true ? "set; " : string.Empty)}}}";
                return new PropertyDto(
                    property.Name,
                    typeName,
                    $"{property.Name}: {typeName} {accessors}",
                    property.GetMethod?.IsPublic == true,
                    property.SetMethod?.IsPublic == true,
                    TypeNameFormatter.IsCollection(property.PropertyType),
                    info.ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(property.PropertyType) is not null);
            })
            .ToArray();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Where(method => !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.GetParameters().Length)
            .Select(method =>
            {
                var parameters = method.GetParameters()
                    .Select(parameter => new ParameterDto(
                        parameter.Name ?? "parameter",
                        TypeNameFormatter.Format(parameter.ParameterType, nullability.Create(parameter))))
                    .ToArray();
                var returnType = TypeNameFormatter.Format(method.ReturnType, nullability.Create(method.ReturnParameter));
                return new MethodDto(
                    method.Name,
                    returnType,
                    $"{method.Name}({FormatParameters(parameters)}): {returnType}",
                    parameters);
            })
            .ToArray();

        return new TypeNodeDto(
            GetTypeId(type),
            type.Namespace ?? string.Empty,
            type.Name,
            GetKind(type),
            type.IsAbstract,
            constructors,
            properties,
            methods,
            type.IsEnum ? Enum.GetNames(type) : []);
    }

    private IReadOnlyList<TypeRelationDto> CreateRelations(IReadOnlyCollection<Type> runtimeTypes)
    {
        var typeSet = runtimeTypes.ToHashSet();
        var relations = new Dictionary<(Type Source, Type Target, string Kind), RelationBuilder>();
        var nullability = new NullabilityInfoContext();

        foreach (var source in runtimeTypes)
        {
            if (source.BaseType is not null && typeSet.Contains(source.BaseType))
            {
                Add(source, source.BaseType, "inheritance", "Basisklasse", null);
            }

            foreach (var target in GetDirectInterfaces(source).Where(typeSet.Contains))
            {
                Add(source, target, "implementation", "Interface", null);
            }

            foreach (var property in source.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var target in FindCatalogTypes(property.PropertyType, typeSet))
                {
                    var propertyNullability = nullability.Create(property);
                    var multiplicity = TypeNameFormatter.IsCollection(property.PropertyType)
                        ? "0..*"
                        : propertyNullability.ReadState == NullabilityState.Nullable || Nullable.GetUnderlyingType(property.PropertyType) is not null
                            ? "0..1"
                            : "1";
                    Add(source, target, "association", property.Name, multiplicity);
                }
            }

            foreach (var constructor in source.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var target in FindCatalogTypes(parameter.ParameterType, typeSet))
                    {
                        Add(source, target, "dependency", $"ctor: {parameter.Name}", null);
                    }
                }
            }

            foreach (var method in source.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName))
            {
                foreach (var parameter in method.GetParameters())
                {
                    foreach (var target in FindCatalogTypes(parameter.ParameterType, typeSet))
                    {
                        Add(source, target, "dependency", $"{method.Name}({parameter.Name})", null);
                    }
                }

                foreach (var target in FindCatalogTypes(method.ReturnType, typeSet))
                {
                    Add(source, target, "dependency", $"{method.Name}: return", null);
                }
            }
        }

        return relations.Values
            .OrderBy(relation => GetTypeId(relation.Source), StringComparer.Ordinal)
            .ThenBy(relation => relation.Kind, StringComparer.Ordinal)
            .ThenBy(relation => GetTypeId(relation.Target), StringComparer.Ordinal)
            .Select(relation => new TypeRelationDto(
                $"{relation.Kind}:{GetTypeId(relation.Source)}->{GetTypeId(relation.Target)}",
                GetTypeId(relation.Source),
                GetTypeId(relation.Target),
                relation.Kind,
                relation.Labels.OrderBy(label => label, StringComparer.Ordinal).ToArray(),
                relation.Multiplicities.Count == 0 ? null : string.Join(", ", relation.Multiplicities.OrderBy(value => value, StringComparer.Ordinal))))
            .ToArray();

        void Add(Type source, Type target, string kind, string label, string? multiplicity)
        {
            if (source == target && kind == "dependency")
            {
                return;
            }

            var key = (source, target, kind);
            if (!relations.TryGetValue(key, out var builder))
            {
                builder = new RelationBuilder(source, target, kind);
                relations[key] = builder;
            }

            builder.Labels.Add(label);
            if (multiplicity is not null)
            {
                builder.Multiplicities.Add(multiplicity);
            }
        }
    }

    private static IEnumerable<Type> FindCatalogTypes(Type type, HashSet<Type> catalog)
    {
        if (catalog.Contains(type))
        {
            yield return type;
        }

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var match in FindCatalogTypes(element, catalog))
            {
                yield return match;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var match in FindCatalogTypes(argument, catalog))
                {
                    yield return match;
                }
            }
        }
    }

    private static IEnumerable<Type> GetDirectInterfaces(Type type)
    {
        var all = type.GetInterfaces();
        var inherited = (type.BaseType?.GetInterfaces() ?? [])
            .Concat(all.SelectMany(candidate => candidate.GetInterfaces()))
            .ToHashSet();
        return all.Where(candidate => !inherited.Contains(candidate));
    }

    private string CreateTypeId(Type type) => $"{type.Assembly.GetName().Name}:{type.FullName ?? type.Name}";

    private static string FormatParameters(IEnumerable<ParameterDto> parameters) =>
        string.Join(", ", parameters.Select(parameter => $"{parameter.TypeName} {parameter.Name}"));

    private static string GetKind(Type type) => type.IsInterface
        ? "interface"
        : type.IsEnum
            ? "enum"
            : type.IsAbstract
                ? "abstractClass"
                : "class";

    private sealed record RelationBuilder(Type Source, Type Target, string Kind)
    {
        public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Multiplicities { get; } = new(StringComparer.Ordinal);
    }
}

public sealed class CommandException(string message) : Exception(message);
