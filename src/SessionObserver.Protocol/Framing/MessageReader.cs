using System.Text.Json;
using SessionObserver.Protocol.Messages;

namespace SessionObserver.Protocol.Framing;

/// <summary>
/// Reads "\n"-terminated JSON messages from a stream. Not thread-safe: use one reader per stream
/// from a single read loop.
/// </summary>
public sealed class MessageReader
{
    public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;

    internal static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _stream;
    private readonly int _maxMessageBytes;
    private byte[] _buffer = new byte[8 * 1024];
    private int _start;
    private int _end;

    public MessageReader(Stream stream, int maxMessageBytes = DefaultMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageBytes, 1);
        _stream = stream;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>
    /// Returns the next message, or null when the stream ends cleanly between messages.
    /// </summary>
    /// <exception cref="ProtocolException">The message is malformed, too large, or the stream ended mid-message.</exception>
    public async ValueTask<ProtocolMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            int newLine = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newLine >= 0)
            {
                int lineStart = _start;
                _start = newLine + 1;
                ProtocolMessage? message = Parse(lineStart, newLine - lineStart);
                if (message is not null)
                {
                    return message;
                }
                continue; // blank line: tolerated and skipped
            }

            if (_end - _start > _maxMessageBytes)
            {
                throw new ProtocolException($"Message exceeds the maximum of {_maxMessageBytes} bytes.");
            }

            MakeRoom();

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end > _start)
                {
                    throw new ProtocolException("The stream ended in the middle of a message.");
                }
                return null;
            }
            _end += read;
        }
    }

    private void MakeRoom()
    {
        int pending = _end - _start;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, pending);
            _start = 0;
            _end = pending;
        }
        if (_end == _buffer.Length)
        {
            // Allow one byte past the limit so an oversized message is detected rather than looping forever.
            int newSize = (int)Math.Min((long)_buffer.Length * 2, (long)_maxMessageBytes + 1);
            Array.Resize(ref _buffer, Math.Max(newSize, _buffer.Length + 1));
        }
    }

    private ProtocolMessage? Parse(int offset, int length)
    {
        ReadOnlySpan<byte> line = _buffer.AsSpan(offset, length);
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }
        if (line.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.ProtocolMessage)
                ?? throw new ProtocolException("Received a null message.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Received a malformed message: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ProtocolException($"Received an unsupported message: {ex.Message}", ex);
        }
    }
}
