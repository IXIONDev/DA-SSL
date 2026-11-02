using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace SSLExceptionManager;

internal sealed class NativeMessaging(Stream input, Stream output)
{
    public const int MaxMessageBytes = 1024 * 1024;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        if (!await ReadExactlyOrEofAsync(input, header, cancellationToken)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
            throw new InvalidDataException("Invalid native message length.");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }

    public async Task WriteAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxMessageBytes) throw new InvalidDataException("Response is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(payload, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("Incomplete native message header.");
            }
            offset += read;
        }
        return true;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException("Incomplete native message payload.");
            offset += read;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
