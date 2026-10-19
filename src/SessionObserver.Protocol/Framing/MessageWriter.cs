using System.Text.Json;
using SessionObserver.Protocol.Messages;

namespace SessionObserver.Protocol.Framing;

/// <summary>
/// Writes one JSON message per line ("\n"-terminated) to a stream. Safe to call from several threads:
/// writes are serialized so messages never interleave.
/// </summary>
public sealed class MessageWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MessageWriter(Stream stream, int maxMessageBytes = MessageReader.DefaultMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageBytes, 1);
        _stream = stream;
        _maxMessageBytes = maxMessageBytes;
    }

    public async Task WriteAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // JSON never contains a raw newline (strings escape it), so "\n" is an unambiguous terminator.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ProtocolMessage);
        if (payload.Length > _maxMessageBytes)
        {
            throw new ProtocolException(
                $"Message of {payload.Length} bytes exceeds the maximum of {_maxMessageBytes} bytes.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(MessageReader.NewLine, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
