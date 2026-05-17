using System;
using System.Collections.Generic;


// Manages a mutable input buffer driven by individual keystrokes.
// No console I/O is performed here; the caller renders Buffer however it likes.
// The caller feeds keystrokes via Feed() and reads Buffer at any time.
// Autocomplete candidates are provided on Tab by the injected completionProvider.
//
// Feed() return value:
//   null              — still editing; bool indicates whether Buffer is non-empty
//   (false, null)     — Escape pressed; buffer was cleared
//   (true,  string)   — Enter pressed; string is the submitted text (may be empty)
public class InputService
{
    private readonly Func<string, List<string>> _completionProvider;

    private string _buffer = string.Empty;
    private List<string>? _suggestions;
    private int _suggestionIndex;

    // The current text being edited. Updated after every Feed() call.
    public string Buffer => _buffer;

    public InputService(Func<string, List<string>> completionProvider)
    {
        _completionProvider = completionProvider;
    }

    // Process one keystroke and update Buffer.
    // Returns null while editing (bool = buffer is non-empty).
    // Returns (false, null) on Escape.
    // Returns (true, text) on Enter with the submitted text.
    public (bool hasContent, string? submitted) Feed(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            string value = _buffer;
            Reset();
            return (true, value);
        }

        if (key.Key == ConsoleKey.Escape)
        {
            Reset();
            return (false, null);
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_buffer.Length > 0)
            {
                _buffer = _buffer.Substring(0, _buffer.Length - 1);
            }
            _suggestions = null;
            return (_buffer.Length > 0, null);
        }

        if (key.Key == ConsoleKey.Tab)
        {
            if (_suggestions == null)
            {
                _suggestions = _completionProvider(_buffer);
                _suggestionIndex = 0;
            }
            else
            {
                _suggestionIndex = (_suggestionIndex + 1) % (_suggestions.Count == 0 ? 1 : _suggestions.Count);
            }

            if (_suggestions.Count > 0)
            {
                _buffer = _suggestions[_suggestionIndex];
            }

            return (_buffer.Length > 0, null);
        }

        if (!char.IsControl(key.KeyChar))
        {
            _buffer += key.KeyChar;
            _suggestions = null;
        }

        return (_buffer.Length > 0, null);
    }

    // Resets buffer and autocomplete state.
    private void Reset()
    {
        _buffer = string.Empty;
        _suggestions = null;
        _suggestionIndex = 0;
    }
}
