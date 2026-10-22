namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Classifies an input value of type <typeparamref name="TIn"/> into a string label. A schema's
/// <c>[Bucket(nameof(X))]</c> attribute names a <c>public static readonly IBucketer&lt;TIn&gt;</c>
/// on the same type; the compiled process extracts the field as <typeparamref name="TIn"/> once and
/// hands the typed value here. Returning <c>null</c> means "no bucket matches," and the applier
/// drops the whole record as <see cref="DropReason.MalformedRecord"/>.
///
/// The output is always a string because a bucket label must describe the bucketing methodology
/// the researcher is reading against (the range, the equivalence class, the policy that assigned
/// it). A numeric or enum output would hide that methodology behind a value the reader has to
/// cross-reference.
/// </summary>
public interface IBucketer<in TIn>
{
    string? Classify(TIn value);
}
