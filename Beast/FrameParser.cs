using System.Collections.Generic;
using System.Text;


// Parses application-level frames from a raw byte stream.
// Wire format produced by Frame.ToWire(): [type,length]content---
// Feed bytes incrementally; call TakeFrames() to drain completed frames.
public class FrameParser
{
    private readonly StringBuilder _buf = new StringBuilder();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly List<(FrameType Type, string Content)> _ready = new List<(FrameType Type, string Content)>();

    public void Feed(byte[] data, int count)
    {
        int charCount = _decoder.GetCharCount(data, 0, count);
        char[] chars = new char[charCount];
        _decoder.GetChars(data, 0, count, chars, 0);
        _buf.Append(chars);
        Parse();
    }

    // Returns and clears all completed frames accumulated since the last call.
    public List<(FrameType Type, string Content)> TakeFrames()
    {
        List<(FrameType Type, string Content)> result = new List<(FrameType Type, string Content)>(_ready);
        _ready.Clear();
        return result;
    }

    private void Parse()
    {
        while (true)
        {
            string s = _buf.ToString();
            int start = s.IndexOf('[');

            if (start < 0)
            {
                _buf.Clear();
                return;
            }

            if (start > 0)
            {
                _buf.Remove(0, start);
                s = _buf.ToString();
            }

            int end = s.IndexOf(']');
            if (end < 0) return;

            string[] parts = s.Substring(1, end - 1).Split(',');
            if (parts.Length != 2 || !byte.TryParse(parts[0], out byte typeByte) || !int.TryParse(parts[1], out int length))
            {
                // Corrupt header — skip past this '[' and retry.
                _buf.Remove(0, 1);
                continue;
            }

            int contentStart = end + 1;
            int needed = contentStart + length + 3; // 3 = "---"
            if (s.Length < needed) return;

            string content = s.Substring(contentStart, length);
            if (s.Substring(contentStart + length, 3) != "---")
            {
                _buf.Remove(0, 1);
                continue;
            }

            _ready.Add(((FrameType)typeByte, content));
            _buf.Remove(0, needed);
        }
    }
}
