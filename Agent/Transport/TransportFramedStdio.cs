using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Wire-level outbound message.
internal readonly struct Frame
{
    public readonly FrameType Type;
    public readonly string Content;

    public Frame(FrameType type, string content)
    {
        Type = type;
        Content = content ?? "";
    }

    // Serialize: [type,length]content---
    internal string ToWire()
    {
        return $"[{(byte)Type},{Content.Length}]{Content}---";
    }
}

// Default implementation: Console stdin/stdout with the [type,length]content--- protocol.
public class TransportFramedStdio : IFramedTransport
{
    private static readonly object _writeLock = new();
    private readonly Stream _stdin = Console.OpenStandardInput();

    public void Send(FrameType type, string text)
    {
        Frame frame = new Frame(type, text);
        lock (_writeLock)
        {
            Console.Write(frame.ToWire());
            Console.Out.Flush();
        }
    }

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        return ReadFrameContentAsync(_stdin, cancellationToken);
    }

    private static async Task<string?> ReadFrameContentAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Scan for '[' to find the frame start, discarding any stray bytes (allows resync after a bad frame).
        int b;
        do
        {
            b = await ReadByteAsync(stream, cancellationToken);
            if (b == -1) return null; // clean EOF
        }
        while ((char)b != '[');

        // Read header bytes up to the closing ']'.
        List<byte> header = new List<byte>();
        while (true)
        {
            b = await ReadByteAsync(stream, cancellationToken);
            if (b == -1) throw new InvalidDataException("Unexpected EOF inside frame header.");
            if ((char)b == ']') break;
            header.Add((byte)b);
        }

        string headerStr = Encoding.UTF8.GetString(header.ToArray());
        int comma = headerStr.IndexOf(',');
        if (comma < 0) throw new InvalidDataException($"Malformed frame header: '[{headerStr}]'.");

        if (!int.TryParse(headerStr.Substring(comma + 1), out int length) || length < 0)
            throw new InvalidDataException($"Invalid content length in frame header: '[{headerStr}]'.");

        byte[] contentBytes = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(contentBytes.AsMemory(totalRead, length - totalRead), cancellationToken);
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (read == 0) throw new InvalidDataException($"Unexpected EOF reading frame content ({totalRead}/{length} bytes read).");
            totalRead += read;
        }

        string content = Encoding.UTF8.GetString(contentBytes);

        if (!await ReadTerminatorAsync(stream, cancellationToken))
            throw new InvalidDataException("Frame terminator '---' not found or malformed.");

        return content;
    }

    // Reads exactly 3 bytes and validates they are all '-', as required by the wire format.
    private static async Task<bool> ReadTerminatorAsync(Stream stream, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 3; i++)
        {
            int b = await ReadByteAsync(stream, cancellationToken);
            if (b != (byte)'-') return false;
        }
        return true;
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, 0, 1, cancellationToken);
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return read == 0 ? -1 : buffer[0];
    }
}
