using System.Reflection;
using System.Text.Json;
using Bit.Telemetee.Telemetry.TelemetryData;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>Builds a <see cref="TelemetrySchema"/> from a POCO marked with <see cref="TelemetryAttribute"/>.</summary>
public static class SchemaBuilder
{
    /// <summary>
    /// Discovers every type in <paramref name="assembly"/> marked with <see cref="TelemetryAttribute"/>
    /// and builds a schema for each. Adding a telemetry type is done through adding the class file under
    /// <c>TelemetryData/</c>.
    /// </summary>
    public static IEnumerable<TelemetrySchema> FromAssembly(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TelemetryAttribute>() is not null)
            .Select(FromType);

    public static TelemetrySchema FromType(Type type)
    {
        var schemaAttr = type.GetCustomAttribute<TelemetryAttribute>()
            ?? throw new InvalidOperationException($"{type.FullName} is missing [Telemetry(\"...\")].");

        if (!typeof(TelemetryRecord).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"{type.FullName} is marked [Telemetry] but does not inherit {nameof(TelemetryRecord)}. " +
                "Every telemetry schema POCO must derive from TelemetryRecord so the OpenAPI document " +
                "and generated clients see it as a polymorphic variant.");
        }

        var fields = new Dictionary<string, FieldTreatment>();
        var sources = new Dictionary<string, string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var treatment = property.GetCustomAttribute<TreatmentAttribute>();
            if (treatment is null)
            {
                throw new InvalidOperationException(
                    $"{type.FullName}.{property.Name} has no treatment attribute. Every property on a " +
                    "telemetry schema must declare one of [Keep], [Bucket], [Pseudonymize], or [Authed].");
            }
            var wireFieldName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            if (sources.TryGetValue(wireFieldName, out var earlier))
            {
                throw new InvalidOperationException(
                    $"{type.FullName}.{property.Name} and {type.FullName}.{earlier} both map to the " +
                    $"snake_case field '{wireFieldName}'. Rename one so each wire field has exactly one " +
                    "declared treatment.");
            }
            fields[wireFieldName] = Resolve(type, treatment);
            sources[wireFieldName] = property.Name;
        }

        return new TelemetrySchema(schemaAttr.Name, fields);
    }

    private static FieldTreatment Resolve(Type declaringType, TreatmentAttribute attribute) => attribute switch
    {
        KeepAttribute => new FieldTreatment.Keep(),
        PseudonymizeAttribute => new FieldTreatment.Pseudonymize(),
        AuthedAttribute authed => new FieldTreatment.Authed(authed.JwtClaim),
        BucketAttribute bucket => new FieldTreatment.Bucket(ReadBucketer(declaringType, bucket.BucketerMember)),
        _ => throw new InvalidOperationException($"Unknown treatment: {attribute.GetType().Name}"),
    };

    private static IBucketer ReadBucketer(Type declaringType, string memberName)
    {
        var member = declaringType.GetMember(memberName, BindingFlags.Public | BindingFlags.Static).SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"{declaringType.FullName}.{memberName} not found; [Bucket] needs a public static readonly IBucketer.");
        var value = member switch
        {
            FieldInfo field => field.GetValue(null),
            PropertyInfo property => property.GetValue(null),
            _ => null,
        };
        return value switch
        {
            IBucketer bucketer => bucketer,
            _ => throw new InvalidOperationException(
                $"{declaringType.FullName}.{memberName} must be an IBucketer."),
        };
    }
}
