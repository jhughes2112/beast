using System;
using System.Threading;
using System.Threading.Tasks;


// Non-interactive display: prints output and errors to stdout/stderr, streams line by line.
// Blocks in RunAsync until cancellation fires.
public class DisplayConsole : IDisplay
{
    private int _streamIndex = -1;
    private bool _didStream = false;

    public void Attach(ConversationModel model)
    {
        model.MessageUpdated += OnMessageUpdated;
    }

    public void SetStatus(string text)
    {
        Console.Error.WriteLine($"[status] {text}");
    }

    public void OnStreamStart(int streamIndex)
    {
        _streamIndex = streamIndex;
        _didStream = false;
    }

    public void OnStreamChunk(string chunk)
    {
        Console.Write(chunk);
        _didStream = true;
    }

    public void OnStreamEnd()
    {
        if (_didStream)
            Console.WriteLine();
        _streamIndex = -1;
        _didStream = false;
    }

    public void SetSendAsync(Func<string, Task> sendAsync)
    {
        // No interactive input in non-interactive mode.
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private void OnMessageUpdated(DisplayMessage msg)
    {
        if (msg.Index == _streamIndex) return;  // streaming is handled in OnStreamChunk/OnStreamEnd
        if (_didStream)
        {
            _didStream = false;
            return;  // streaming already printed this output line-by-line
        }
        if (msg.Type == FrameType.Output && !string.IsNullOrEmpty(msg.Content))
            Console.WriteLine(msg.Content);
        else if (msg.Type == FrameType.Error)
            Console.Error.WriteLine($"[error] {msg.Content}");
    }
}
