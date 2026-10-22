using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Bit.Telemetee.Telemetry.TelemetryData;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Walks an assembly's <c>[Telemetry]</c>-marked input types and produces a
/// <see cref="SchemaDescriptor"/> for each. For every schema, SchemaBuilder reads the input
/// POCO's attributes, resolves the nested <c>Sink</c>, and compiles a
/// <see cref="ProcessFunc"/> whose body is a single <c>new Sink { ... }</c> initializer with one
/// bound property per declared treatment.
/// 
/// Validation is strict and fails at startup. Every schema must comply.
/// <list type="bullet">
/// <item>inherit from <see cref="TelemetryRecord"/></item>
/// <item>nested <c>Sink</c> class for each Telemetry class</item>
/// <item><c>Sink</c> inherits from <see cref="TelemetrySinkRecord"/></item>
/// <item><c>Sink</c> has a parameterless constructor</item>
/// <item>every input property declares a treatment attribute (<c>[Keep]</c>, <c>[Bucket]</c>,
/// <c>[Pseudonymize]</c>, or <c>[Authed]</c>)</item>
/// <item>input properties do not collide on their snake_case wire names</item>
/// <item>matching sink property for each input property</item>
/// <item>type compatibility between attribute-marked record properties and their associated sink
/// properties, given the requested treatment</item>
/// <item><c>[Bucket]</c> members resolve to a non-null public static
/// <see cref="IBucketer{T}"/> with a matching <c>Treatments.Bucket</c> overload</item>
/// <item>declared sink retention time and implementation of <see cref="IRetained"/></item>
/// </list>
/// </summary>
internal static class SchemaBuilder
{
    private const string NestedSinkTypeName = "Sink";

    public static IEnumerable<SchemaDescriptor> FromAssembly(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TelemetryAttribute>() is not null)
            .Select(FromType);

    public static SchemaDescriptor FromType(Type inputType)
    {
        var schemaAttr = inputType.GetCustomAttribute<TelemetryAttribute>()
            ?? throw new InvalidOperationException($"{inputType.FullName} is missing [Telemetry(\"...\")].");

        if (!typeof(TelemetryRecord).IsAssignableFrom(inputType))
        {
            throw new InvalidOperationException(
                $"{inputType.FullName} is marked [Telemetry] but does not inherit {nameof(TelemetryRecord)}. " +
                "Every telemetry schema's input POCO must derive from TelemetryRecord so the OpenAPI document " +
                "and generated clients see it as a polymorphic variant.");
        }

        var sinkType = inputType.GetNestedType(NestedSinkTypeName)
            ?? throw new InvalidOperationException(
                $"{inputType.FullName} has no nested '{NestedSinkTypeName}' type. Each [Telemetry] input " +
                "must declare its sink shape as a public nested class named 'Sink'.");

        if (!typeof(TelemetrySinkRecord).IsAssignableFrom(sinkType))
        {
            throw new InvalidOperationException(
                $"{sinkType.FullName} must inherit {nameof(TelemetrySinkRecord)}.");
        }

        var retention = ReadRetention(sinkType);
        var inputProperties = CollectInputProperties(inputType);
        var sinkProperties = CollectSinkProperties(sinkType);
        var isAuthed = inputProperties.Any(f => f.Treatment is AuthedAttribute);

        var process = CompileProcess(inputType, sinkType, inputProperties, sinkProperties);
        return new SchemaDescriptor(
            schemaAttr.Name,
            isAuthed,
            inputType,
            sinkType,
            retention,
            process);
    }

    private static TimeSpan ReadRetention(Type sinkType)
    {
        if (!sinkType.GetInterfaces().Any(i => i == typeof(IRetained)))
        {
            throw new InvalidOperationException(
                $"{sinkType.FullName} must implement {nameof(IRetained)} to declare its storage lifetime.");
        }

        // IRetained.RetentionTime is a static abstract. Reach the implementor's static getter via
        // normal reflection on the sink type itself; interface mapping is not needed because static
        // abstract members are declared directly on the implementing type.
        var property = sinkType.GetProperty(
            nameof(IRetained.RetentionTime),
            BindingFlags.Public | BindingFlags.Static);
        if (property is null || property.PropertyType != typeof(TimeSpan))
        {
            throw new InvalidOperationException(
                $"{sinkType.FullName} must declare 'public static TimeSpan {nameof(IRetained.RetentionTime)}' " +
                $"to satisfy {nameof(IRetained)}.");
        }
        return (TimeSpan)property.GetValue(null)!;
    }

    private static List<InputProperty> CollectInputProperties(Type inputType)
    {
        var records = new List<InputProperty>();
        var byWireName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var treatment = property.GetCustomAttribute<TreatmentAttribute>();
            if (treatment is null)
            {
                throw new InvalidOperationException(
                    $"{inputType.FullName}.{property.Name} has no treatment attribute. Every property on a " +
                    "telemetry schema must declare one of [Keep], [Bucket], [Pseudonymize], or [Authed].");
            }
            var wireName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            if (byWireName.TryGetValue(wireName, out var earlier))
            {
                throw new InvalidOperationException(
                    $"{inputType.FullName}.{property.Name} and {inputType.FullName}.{earlier} both map to " +
                    $"the snake_case field '{wireName}'. Rename one so each wire field has exactly one " +
                    "declared treatment.");
            }
            byWireName[wireName] = property.Name;
            records.Add(new InputProperty(property, wireName, treatment));
        }
        return records;
    }

    private static Dictionary<string, PropertyInfo> CollectSinkProperties(Type sinkType)
    {
        var inherited = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(TelemetrySinkRecord.BatchTime),
            nameof(TelemetrySinkRecord.Seq),
        };
        var byWireName = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in sinkType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (inherited.Contains(property.Name))
            {
                continue;
            }
            var wireName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            byWireName[wireName] = property;
        }
        return byWireName;
    }

    /// <summary>
    /// Compiles the per-schema <see cref="ProcessFunc"/>. The body is a single
    /// <c>new Sink { property = Treatments.X(root, "field", ...), ... }</c> expression; one
    /// binding per input property. The first binding whose helper throws <see cref="DropException"/>
    /// unwinds past the rest, which is why the member-init form stays valid even when a treatment
    /// aborts the record mid-flight.
    /// </summary>
    private static ProcessFunc CompileProcess(
        Type inputType,
        Type sinkType,
        List<InputProperty> inputProperties,
        Dictionary<string, PropertyInfo> sinkProperties)
    {
        var paramRoot = Expression.Parameter(typeof(JsonElement), "root");
        var paramContext = Expression.Parameter(typeof(TreatmentContext), "context");

        var bindings = new List<MemberBinding>(inputProperties.Count);
        foreach (var inputProperty in inputProperties)
        {
            if (!sinkProperties.TryGetValue(inputProperty.WireName, out var sinkProperty))
            {
                throw new InvalidOperationException(
                    $"{sinkType.FullName} has no property matching the input field '{inputProperty.WireName}' " +
                    $"(from {inputType.FullName}.{inputProperty.Property.Name}). Every input property must have a " +
                    "parallel property on the Sink.");
            }
            var treatmentCall = BuildTreatmentCall(inputType, inputProperty, sinkProperty, paramRoot, paramContext);
            bindings.Add(Expression.Bind(sinkProperty, treatmentCall));
        }

        var ctor = sinkType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"{sinkType.FullName} must have a parameterless constructor so the compiled process can " +
                "build it via a member initializer.");

        var init = Expression.MemberInit(Expression.New(ctor), bindings);
        var upcast = Expression.Convert(init, typeof(TelemetrySinkRecord));
        return Expression.Lambda<ProcessFunc>(upcast, paramRoot, paramContext).Compile();
    }

    private static Expression BuildTreatmentCall(
        Type inputType,
        InputProperty inputProperty,
        PropertyInfo sinkProperty,
        ParameterExpression root,
        ParameterExpression context)
    {
        var wireName = Expression.Constant(inputProperty.WireName);
        return inputProperty.Treatment switch
        {
            KeepAttribute => BuildKeepCall(inputType, inputProperty, sinkProperty, root, wireName),
            PseudonymizeAttribute => RequireStringToString(inputType, inputProperty, sinkProperty, "[Pseudonymize]",
                Expression.Call(TreatmentsMethod(nameof(Treatments.PseudonymizeString)), root, wireName, context)),
            AuthedAttribute authed => RequireStringToString(inputType, inputProperty, sinkProperty, "[Authed]",
                Expression.Call(TreatmentsMethod(nameof(Treatments.Authed)), root, wireName, Expression.Constant(authed.JwtClaim), context)),
            BucketAttribute bucket => BuildBucketCall(inputType, inputProperty, sinkProperty, bucket, root, wireName),
            _ => throw new InvalidOperationException(
                $"{inputType.FullName}.{inputProperty.Property.Name} has unsupported treatment " +
                $"{inputProperty.Treatment.GetType().Name}."),
        };
    }

    private static Expression BuildKeepCall(
        Type inputType,
        InputProperty inputProperty,
        PropertyInfo sinkProperty,
        ParameterExpression root,
        ConstantExpression wireName)
    {
        if (sinkProperty.PropertyType != inputProperty.Property.PropertyType)
        {
            throw new InvalidOperationException(
                $"{inputType.FullName}.{inputProperty.Property.Name} is [Keep] (type {inputProperty.Property.PropertyType.Name}) " +
                $"but the matching sink property is {sinkProperty.PropertyType.Name}. [Keep] requires both to be the same type.");
        }
        var methodName = KeepMethodNameFor(inputProperty.Property.PropertyType)
            ?? throw new InvalidOperationException(
                $"{inputType.FullName}.{inputProperty.Property.Name} is [Keep] with type {inputProperty.Property.PropertyType.Name}, " +
                "which has no corresponding Treatments.Keep* helper. Add one if this type should be supported.");
        return Expression.Call(TreatmentsMethod(methodName), root, wireName);
    }

    private static string? KeepMethodNameFor(Type type)
    {
        if (type == typeof(string))
        {
            return nameof(Treatments.KeepString);
        }

        if (type == typeof(bool))
        {
            return nameof(Treatments.KeepBool);
        }

        if (type == typeof(int))
        {
            return nameof(Treatments.KeepInt);
        }

        if (type == typeof(long))
        {
            return nameof(Treatments.KeepLong);
        }

        if (type == typeof(float))
        {
            return nameof(Treatments.KeepFloat);
        }

        if (type == typeof(double))
        {
            return nameof(Treatments.KeepDouble);
        }

        return null;
    }

    private static Expression BuildBucketCall(
        Type inputType,
        InputProperty inputProperty,
        PropertyInfo sinkProperty,
        BucketAttribute bucket,
        ParameterExpression root,
        ConstantExpression wireName)
    {
        var member = inputType.GetMember(
            bucket.BucketerMember,
            BindingFlags.Public | BindingFlags.Static).SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"{inputType.FullName}.{bucket.BucketerMember} not found; [Bucket] needs a public static " +
                $"readonly {nameof(IBucketer<object>)} member on the declaring type.");
        var instance = member switch
        {
            FieldInfo f => f.GetValue(null),
            PropertyInfo p => p.GetValue(null),
            _ => null,
        };
        if (instance is null)
        {
            throw new InvalidOperationException(
                $"{inputType.FullName}.{bucket.BucketerMember} must be a non-null static bucketer.");
        }
        var bucketerInterface = instance.GetType().GetInterfaces()
            .SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBucketer<>))
            ?? throw new InvalidOperationException(
                $"{inputType.FullName}.{bucket.BucketerMember} ({instance.GetType().Name}) does not implement " +
                $"{nameof(IBucketer<object>)}.");

        var bucketIn = bucketerInterface.GetGenericArguments()[0];

        if (inputProperty.Property.PropertyType != bucketIn)
        {
            throw new InvalidOperationException(
                $"{inputType.FullName}.{inputProperty.Property.Name} is {inputProperty.Property.PropertyType.Name} but the " +
                $"bucketer {bucket.BucketerMember} takes {bucketIn.Name}. The input property type must equal " +
                "the bucketer's input type.");
        }
        if (sinkProperty.PropertyType != typeof(string))
        {
            throw new InvalidOperationException(
                $"{sinkProperty.DeclaringType!.FullName}.{sinkProperty.Name} is {sinkProperty.PropertyType.Name} " +
                $"but [Bucket] sink properties must be string: a bucket label describes the methodology, so the " +
                "sink always receives the human-readable label the bucketer emits.");
        }

        var method = typeof(Treatments).GetMethods()
            .FirstOrDefault(m =>
                m.Name == nameof(Treatments.Bucket) &&
                m.GetParameters().Length == 3 &&
                m.GetParameters()[2].ParameterType == bucketerInterface)
            ?? throw new InvalidOperationException(
                $"No Treatments.Bucket overload accepts {bucketerInterface.Name}. Add one or change the " +
                "bucketer's TIn to a supported type.");

        return Expression.Call(
            method,
            root,
            wireName,
            Expression.Constant(instance, bucketerInterface));
    }

    private static Expression RequireStringToString(
        Type inputType,
        InputProperty inputProperty,
        PropertyInfo sinkProperty,
        string treatmentLabel,
        Expression call)
    {
        if (inputProperty.Property.PropertyType != typeof(string) || sinkProperty.PropertyType != typeof(string))
        {
            throw new InvalidOperationException(
                $"{inputType.FullName}.{inputProperty.Property.Name} is {treatmentLabel} but the input or sink property " +
                $"is not string (input: {inputProperty.Property.PropertyType.Name}, sink: {sinkProperty.PropertyType.Name}). " +
                $"{treatmentLabel} properties must be string on both sides.");
        }
        return call;
    }

    private static MethodInfo TreatmentsMethod(string name) =>
        typeof(Treatments).GetMethod(name)
            ?? throw new InvalidOperationException($"Treatments.{name} not found.");

    private sealed record InputProperty(PropertyInfo Property, string WireName, TreatmentAttribute Treatment);
}
