using System.Reflection;

namespace ClassGraph.Server;

public static class TypeNameFormatter
{
    private static readonly IReadOnlyDictionary<Type, string> Aliases = new Dictionary<Type, string>
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object"
    };

    public static string Format(Type type, NullabilityInfo? nullability = null)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (nullableUnderlying is not null)
        {
            return $"{Format(nullableUnderlying)}?";
        }

        string result;
        if (Aliases.TryGetValue(type, out var alias))
        {
            result = alias;
        }
        else if (type.IsArray)
        {
            result = $"{Format(type.GetElementType()!, nullability?.ElementType)}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        else if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            var arguments = type.GetGenericArguments();
            var nullabilityArguments = nullability?.GenericTypeArguments ?? [];
            var formatted = arguments.Select((argument, index) =>
                Format(argument, index < nullabilityArguments.Length ? nullabilityArguments[index] : null));
            result = $"{name}<{string.Join(", ", formatted)}>";
        }
        else
        {
            result = type.Name;
        }

        if (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable)
        {
            result += "?";
        }

        return result;
    }

    public static bool IsCollection(Type type)
    {
        if (type == typeof(string))
        {
            return false;
        }

        return type.IsArray || type.GetInterfaces()
            .Concat([type])
            .Any(candidate => candidate.IsGenericType &&
                              candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    public static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.GetInterfaces()
            .Concat([type])
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                                         candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }
}
