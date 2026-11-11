using Bit.Telemetee.Telemetry.Pseudonymization;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Runtime input the compiled process delegate needs beyond the raw JSON: the current pseudonym
/// salt.
/// </summary>
public sealed record TreatmentContext(PseudonymSalt Salt);
