namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Startup-built lookup from schema name and sink type to the record's full descriptor. The
/// applier reads by name (incoming records carry the wire discriminator); the sink reads by sink
/// type (grouping typed records for Parquet emission). Throws if two descriptors share a name.
/// </summary>
internal sealed class SchemaCatalog
{
    private readonly Dictionary<string, SchemaDescriptor> _byName;
    private readonly Dictionary<Type, SchemaDescriptor> _bySinkType;

    public SchemaCatalog(IEnumerable<SchemaDescriptor> descriptors)
    {
        var materialized = descriptors as IReadOnlyCollection<SchemaDescriptor> ?? [.. descriptors];
        EnsureUniqueNames(materialized);
        _byName = materialized.ToDictionary(d => d.Name, StringComparer.Ordinal);
        _bySinkType = materialized.ToDictionary(d => d.SinkType);
        All = materialized;
    }

    public IReadOnlyCollection<SchemaDescriptor> All { get; }

    public bool TryGetByName(string name, out SchemaDescriptor descriptor) =>
        _byName.TryGetValue(name, out descriptor!);

    public SchemaDescriptor ForSinkType(Type sinkType) => _bySinkType[sinkType];

    /// <summary>
    /// Throws if two descriptors share a <see cref="SchemaDescriptor.Name"/>. The applier
    /// routes records by name, so a duplicate would silently shadow the earlier declaration.
    /// Called at service registration so the error surfaces at host boot, not at the first request.
    /// </summary>
    internal static void EnsureUniqueNames(IEnumerable<SchemaDescriptor> descriptors)
    {
        var seen = new HashSet<string>();
        foreach (var descriptor in descriptors)
        {
            if (!seen.Add(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"Two telemetry schemas declare the name '{descriptor.Name}'. Each " +
                    "[Telemetry(...)] name must be unique.");
            }
        }
    }
}
