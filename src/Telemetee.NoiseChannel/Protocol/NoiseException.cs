namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>Raised when a Noise message is malformed or fails authentication. Carries no detail a peer could use.</summary>
internal sealed class NoiseException(string message) : Exception(message);
