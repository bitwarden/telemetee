using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Text.Json;
using Bit.Telemetee.NoiseChannel;
using Bit.Telemetee.NoiseChannel.Protocol;
using Bit.Telemetee.Telemetry.Batch;
using Bit.Telemetee.Telemetry.Pages;
using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Consumes one page (NK message 1) and produces one empty-payload message 2. One POST, one page,
/// one handshake, and a constant-size reply whatever the page contained, so neither the request
/// nor the response reveals how many records survived or who they belonged to.
/// </summary>
internal sealed class TelemetryHandler
{
    private readonly KeyPair _staticKey;
    private readonly SchemaApplier _applier;
    private readonly BatchBuffer _buffer;
    private readonly TelemetryMetrics _metrics;

    public TelemetryHandler(
        ScopedNoiseKey staticKey,
        SchemaApplier applier,
        BatchBuffer buffer,
        TelemetryMetrics metrics)
    {
        _staticKey = staticKey.KeyPair;
        _applier = applier;
        _buffer = buffer;
        _metrics = metrics;
    }

    public byte[] HandlePage(ReadOnlySpan<byte> page)
    {
        using var responder = new NoiseNkResponder(_staticKey);
        byte[] plaintext;
        try
        {
            plaintext = responder.ReadMessage(page);
        }
        catch (NoiseException)
        {
            _metrics.PageRejectedNoise();
            return ConstantSizeGarbage();
        }

        _metrics.PageAccepted();
        ProcessPage(plaintext);
        return responder.WriteMessage(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] ConstantSizeGarbage()
    {
        // A client whose message 1 was rejected has no handshake state to decrypt a reply with.
        // Returning random bytes keeps the response size equal to the success path so the edge
        // cannot tell handshake failures apart from accepted pages.
        var reply = new byte[PageFormat.ResponseLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(reply);
        return reply;
    }

    private void ProcessPage(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < PageFormat.BodyLengthPrefixBytes)
        {
            _metrics.PageMalformed();
            return;
        }
        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext);
        if (bodyLength == 0 || PageFormat.BodyLengthPrefixBytes + bodyLength > plaintext.Length)
        {
            _metrics.PageMalformed();
            return;
        }
        var body = plaintext.Slice(PageFormat.BodyLengthPrefixBytes, bodyLength).ToArray();

        try
        {
            var reader = new CborReader(body, CborConformanceMode.Lax);
            reader.ReadStartMap();
            bool eventsRead = false;
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                var key = reader.ReadTextString();
                switch (key)
                {
                    case "events":
                        ReadEvents(reader);
                        eventsRead = true;
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }
            reader.ReadEndMap();
            if (!eventsRead)
            {
                // A body without an "events" entry carries no records; nothing to apply.
                return;
            }
        }
        catch (CborContentException)
        {
            _metrics.PageMalformed();
        }
        catch (InvalidOperationException)
        {
            // CborReader throws InvalidOperationException on structural mismatch (e.g. ReadTextString
            // on a non-string value). Treat the same as a content exception.
            _metrics.PageMalformed();
        }
    }

    private void ReadEvents(CborReader reader)
    {
        reader.ReadStartArray();
        while (reader.PeekState() != CborReaderState.EndArray)
        {
            var json = ReadEventAsJson(reader);
            Record(_applier.Apply(json));
        }
        reader.ReadEndArray();
    }

    private static byte[] ReadEventAsJson(CborReader reader)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCborValueAsJson(reader, writer);
        }
        return stream.ToArray();
    }

    private static void WriteCborValueAsJson(CborReader reader, Utf8JsonWriter writer)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                writer.WriteStartObject();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    var key = reader.ReadTextString();
                    writer.WritePropertyName(key);
                    WriteCborValueAsJson(reader, writer);
                }
                reader.ReadEndMap();
                writer.WriteEndObject();
                break;
            case CborReaderState.StartArray:
                reader.ReadStartArray();
                writer.WriteStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    WriteCborValueAsJson(reader, writer);
                }
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;
            case CborReaderState.TextString:
                writer.WriteStringValue(reader.ReadTextString());
                break;
            case CborReaderState.ByteString:
                writer.WriteBase64StringValue(reader.ReadByteString());
                break;
            case CborReaderState.UnsignedInteger:
                writer.WriteNumberValue(reader.ReadUInt64());
                break;
            case CborReaderState.NegativeInteger:
                writer.WriteNumberValue(reader.ReadInt64());
                break;
            case CborReaderState.SinglePrecisionFloat:
                writer.WriteNumberValue(reader.ReadSingle());
                break;
            case CborReaderState.DoublePrecisionFloat:
                writer.WriteNumberValue(reader.ReadDouble());
                break;
            case CborReaderState.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;
            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;
            default:
                reader.SkipValue();
                writer.WriteNullValue();
                break;
        }
    }

    private void Record(SchemaApplyResult result)
    {
        if (result.IsOk)
        {
            if (_buffer.TryAdd(result.Record!))
            {
                _metrics.RecordAccepted();
            }
            else
            {
                _metrics.RecordDropped(ReasonTag(DropReason.BufferFull));
            }
        }
        else
        {
            _metrics.RecordDropped(ReasonTag(result.Reason!.Value));
        }
    }

    private static string ReasonTag(DropReason reason) => reason switch
    {
        DropReason.MalformedJson => "malformed_json",
        DropReason.MalformedRecord => "malformed_record",
        DropReason.UnknownSchema => "unknown_schema",
        DropReason.OversizeRecord => "oversize",
        DropReason.BufferFull => "buffer_full",
        _ => "unknown",
    };
}
