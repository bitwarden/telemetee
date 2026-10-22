namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>Reason a record did not reach the batch buffer. Reported as a metric tag.</summary>
internal enum DropReason
{
    MalformedJson,
    MalformedRecord,
    UnknownSchema,
    OversizeRecord,
    BufferFull,
    /// <summary>Authed schema sent in an unauthed group, or vice versa.</summary>
    WrongAuthScope,
    /// <summary>Authed field was absent from the record, or the token carried no value for the required JWT claim.</summary>
    MissingAuthClaim,
    /// <summary>Authed field was present but did not equal the corresponding JWT claim.</summary>
    ClaimMismatch,
    /// <summary>The JWT accompanying an authed record failed to parse.</summary>
    AuthMalformed,
}
