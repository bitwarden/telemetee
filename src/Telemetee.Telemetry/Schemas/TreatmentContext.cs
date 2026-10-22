using Bit.Telemetee.Telemetry.Pseudonymization;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Runtime inputs the compiled process delegate needs beyond the raw JSON: the current pseudonym
/// salt and, for authed schemas, the verified JWT claims the enclosing authed group arrived with.
/// Unauthed records carry a null <see cref="Claims"/>.
/// </summary>
public sealed record TreatmentContext(PseudonymSalt Salt, IReadOnlyDictionary<string, string>? Claims);
