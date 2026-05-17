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
        var header = new List<byte>();
        while (true)
        {
            int b = await ReadByteAsync(stream, cancellationToken);
            if (b == -1) return null;
            if ((char)b == ']') break;
            header.Add((byte)b);
        }

        if (header.Count == 0 || header[0] != (byte)'[') return null;

        string headerStr = Encoding.UTF8.GetString(header.ToArray(), 1, header.Count - 1);
        int comma = headerStr.IndexOf(',');
        if (comma < 0) return null;

        if (!int.TryParse(headerStr.Substring(comma + 1), out int length) || length < 0) return null;

        byte[] contentBytes = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await stream.ReadAsync(contentBytes.AsMemory(totalRead, length - totalRead), cancellationToken);
            if (read == 0) return null;
            totalRead += read;
        }

        string content = Encoding.UTF8.GetString(contentBytes);

        await ReadUntilAsync(stream, (byte)'-', cancellationToken);

        return content;
    }

    private static async Task<string> ReadUntilAsync(Stream stream, byte terminator, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        while (true)
        {
            int b = await ReadByteAsync(stream, cancellationToken);
            if (b == -1) return string.Empty;
            buffer.Add((byte)b);
            if (b == terminator) break;
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int read = await stream.ReadAsync(buffer, 0, 1, cancellationToken);
        return read == 0 ? -1 : buffer[0];
    }
}
