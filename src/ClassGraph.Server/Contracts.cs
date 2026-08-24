namespace ClassGraph.Server;

public sealed record ParameterDto(string Name, string TypeName);

public sealed record ConstructorDto(string Signature, IReadOnlyList<ParameterDto> Parameters);

public sealed record PropertyDto(
    string Name,
    string TypeName,
    string Signature,
    bool CanRead,
    bool CanWrite,
    bool IsCollection,
    bool IsNullable);

public sealed record MethodDto(
    string Name,
    string ReturnType,
    string Signature,
    IReadOnlyList<ParameterDto> Parameters);

public sealed record TypeNodeDto(
    string Id,
    string Namespace,
    string Name,
    string Kind,
    bool IsAbstract,
    IReadOnlyList<ConstructorDto> Constructors,
    IReadOnlyList<PropertyDto> Properties,
    IReadOnlyList<MethodDto> Methods,
    IReadOnlyList<string> EnumValues);

public sealed record TypeRelationDto(
    string Id,
    string SourceTypeId,
    string TargetTypeId,
    string Kind,
    IReadOnlyList<string> Labels,
    string? Multiplicity);

public sealed record PropertyValueDto(string Name, string TypeName, string Value, bool IsError = false);

public sealed record InstanceNodeDto(
    string Id,
    string TypeId,
    string TypeName,
    IReadOnlyList<PropertyValueDto> PropertyValues);

public sealed record InstanceRelationDto(
    string Id,
    string SourceInstanceId,
    string TargetInstanceId,
    string PropertyName,
    int? CollectionIndex);

public sealed record GraphSnapshotDto(
    long Revision,
    IReadOnlyList<TypeNodeDto> Types,
    IReadOnlyList<TypeRelationDto> TypeRelations,
    IReadOnlyList<InstanceNodeDto> Instances,
    IReadOnlyList<InstanceRelationDto> InstanceRelations);

public sealed record CommandResult(bool Success, string Message, bool Mutated = false, object? Data = null);

public sealed record ServerHelloDto(
    string ServerVersion,
    string AssemblyName,
    string AssemblyPath,
    int ProtocolVersion = 1);

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string Type,
    string? RequestId,
    long? Revision,
    object? Payload);
