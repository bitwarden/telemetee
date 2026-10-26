using Bit.Telemetee.NoiseChannel.Protocol;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// Holds the collector's current Noise static key, rotated on cadence by
/// <see cref="NoiseKeyRotator"/>. A previous key is retained as long as any scope captured it via
/// <see cref="AcquireScoped"/>, so a handler cannot race with
/// <see cref="IDisposable.Dispose"/> on a key it is mid-handshake with. Once the last scope on a
/// retired key releases, the key is disposed immediately — no reliance on finalization to clear
/// its native private-key material.
/// </summary>
internal sealed class NoiseStaticKey
{
    private readonly Lock _lock = new();
    private readonly Dictionary<KeyPair, int> _refs = new(ReferenceEqualityComparer.Instance);
    private KeyPair _currentKey = KeyPair.Generate();

    public NoiseStaticKey()
    {
        _refs[_currentKey] = 0;
    }

    public ScopedNoiseKey AcquireScoped()
    {
        lock (_lock)
        {
            _refs[_currentKey]++;
            return new ScopedNoiseKey(_currentKey, this);
        }
    }

    public void Rotate()
    {
        lock (_lock)
        {
            if (_refs[_currentKey] == 0)
            {
                _refs.Remove(_currentKey);
                _currentKey.Dispose();
            }
            _currentKey = KeyPair.Generate();
            _refs[_currentKey] = 0;
        }
    }

    internal void Release(KeyPair key)
    {
        lock (_lock)
        {
            if (!_refs.TryGetValue(key, out var count)) return;
            count--;
            if (count == 0 && !ReferenceEquals(key, _currentKey))
            {
                _refs.Remove(key);
                key.Dispose();
            }
            else
            {
                _refs[key] = count;
            }
        }
    }
}
