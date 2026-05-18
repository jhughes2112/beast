using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;


// Beast app: launches the Agent container and drives either a TUI or headless console session.
public class BeastApp : IDisposable, IAsyncDisposable
{
    private readonly string _image;
    private readonly List<string> _messages;
    private readonly bool _nonInteractive;

    private CancellationTokenSource? _cts;
    private ConversationModel? _model;
    private ITransportClient? _wsClient;
    private DockerContext? _docker;
    private string? _containerId;

    // Read loop state
    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private int _nextIndex = 0;
    private int _streamIndex = -1;
    private int _committedStreamIndex = -1;  // slot to reuse after StreamEnd
    private FrameType _streamType;
    private string _streamContent = "";
    private string _nonInteractiveLineBuf = "";  // buffered streaming output for non-interactive mode
    private bool _didStreamToConsole = false;    // true when streaming already printed to stdout
    private string? _pingNonce;
    private bool _readyFired;

    private Label? _statusLabel;
    private TextField? _inputField;

    public BeastApp(string image, List<string> messages, bool nonInteractive)
    {
        _image = image;
        _messages = messages;
        _nonInteractive = nonInteractive;
    }

    public async Task<int> Run()
    {
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        _model = new ConversationModel();
        _model.MessageUpdated += OnMessageUpdated;

        int exitCode = 0;
        try
        {
            _docker = new DockerContext();
            string containerName = $"beastagent_{Guid.NewGuid():N}";
            await _docker.RemoveContainerByNameAsync(containerName);
            _containerId = await _docker.LaunchContainerAsync(_image, containerName, new List<string>());

            _wsClient = await RetryConnectAsync("ws://localhost:13131/", _cts.Token);

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoop(_wsClient, _readCts.Token), _readCts.Token);

            if (_nonInteractive)
            {
                try { await Task.Delay(Timeout.Infinite, _cts.Token); }
                catch (OperationCanceledException) { }
            }
            else
            {
                RunTui(_cts.Token);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[beast] Error: {ex.Message}");
            exitCode = 1;
        }

        return exitCode;
    }

    private async Task ReadLoop(ITransportClient wsClient, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? message = await wsClient.ReceiveAsync(token);
                if (message == null) break;

                (FrameType type, string content) = ParseFrame(message);
                ProcessFrame(type, content);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus($"[read error] {ex.Message}");
        }

        SetStatus("Agent disconnected.");
        _cts?.Cancel();
    }

    private static (FrameType Type, string Content) ParseFrame(string message)
    {
        int pipe = message.IndexOf('|');
        if (pipe < 0 || !byte.TryParse(message.Substring(0, pipe), out byte typeByte))
            return (FrameType.Output, message);

        return ((FrameType)typeByte, message.Substring(pipe + 1));
    }

    private void ProcessFrame(FrameType type, string content)
    {
        switch (type)
        {
            case FrameType.StreamStart:
                _streamType = content == StreamTag.Thinking ? FrameType.Thinking : content == StreamTag.Tool ? FrameType.Tool : FrameType.Output;
                _streamContent = "";
                _nonInteractiveLineBuf = "";
                _streamIndex = _nextIndex++;
                _model!.Update(_streamIndex, _streamType, _streamContent);
                break;

            case FrameType.StreamChunk:
                if (_streamIndex < 0) break;
                _streamContent += content;
                _model!.Update(_streamIndex, _streamType, _streamContent);
                if (_nonInteractive)
                {
                    // Buffer and flush complete lines; partial lines wait for the next chunk or StreamEnd.
                    _nonInteractiveLineBuf += content;
                    int nl;
                    while ((nl = _nonInteractiveLineBuf.IndexOf('\n')) >= 0)
                    {
                        Console.WriteLine(_nonInteractiveLineBuf.Substring(0, nl));
                        _nonInteractiveLineBuf = _nonInteractiveLineBuf.Substring(nl + 1);
                        _didStreamToConsole = true;
                    }
                }
                break;

            case FrameType.StreamEnd:
                _committedStreamIndex = _streamIndex;
                _streamIndex = -1;
                _streamContent = "";
                if (_nonInteractive && _nonInteractiveLineBuf.Length > 0)
                {
                    Console.WriteLine(_nonInteractiveLineBuf);
                    _nonInteractiveLineBuf = "";
                    _didStreamToConsole = true;
                }
                break;

            case FrameType.Status:
                if (content == "ready")
                {
                    _pingNonce = Guid.NewGuid().ToString("N");
                    string nonce = _pingNonce;
                    Task.Run(async () =>
                    {
                        try { await _wsClient!.SendAsync($"/ping {nonce}"); }
                        catch (Exception ex) { SetStatus($"[ping error] {ex.Message}"); }
                    });
                }
                else if (_pingNonce != null && content == $"pong {_pingNonce}" && !_readyFired)
                {
                    _readyFired = true;
                    Task.Run(async () =>
                    {
                        foreach (string msg in _messages)
                            await _wsClient!.SendAsync(msg);
                    });
                }
                else
                {
                    SetStatus(content);
                }
                break;

            default:
                // Reuse the stream slot for the committed frame that immediately follows StreamEnd.
                int slotIndex;
                if (_committedStreamIndex >= 0 && type == _streamType)
                {
                    slotIndex = _committedStreamIndex;
                    _committedStreamIndex = -1;
                }
                else
                {
                    _committedStreamIndex = -1;
                    slotIndex = _nextIndex++;
                }
                _model!.Update(slotIndex, type, content);
                break;
        }
    }

    private void OnMessageUpdated(DisplayMessage msg)
    {
        if (!_nonInteractive) return;
        if (msg.Index == _streamIndex) return;  // skip live stream slot updates (handled in ProcessFrame)
        if (_didStreamToConsole)
        {
            _didStreamToConsole = false;
            return;  // streaming already printed this output line-by-line
        }
        if (msg.Type == FrameType.Output && !string.IsNullOrEmpty(msg.Content))
            Console.WriteLine(msg.Content);
        else if (msg.Type == FrameType.Error)
            Console.Error.WriteLine($"[error] {msg.Content}");
    }

    private void RunTui(CancellationToken cancellationToken)
    {
        Application.Init();

        // When Ctrl+C fires, stop the TUI event loop cleanly.
        cancellationToken.Register(() => Application.RequestStop());

        Toplevel top = Application.Top;
        top.ColorScheme = Colors.Base;

        MessageHistoryView historyView = new MessageHistoryView(_model!)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        // Status bar: one line above the input.
        _statusLabel = new Label("")
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = Colors.Menu
        };

        // Input line pinned at the very bottom.
        _inputField = new TextField("")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
        };

        _inputField.KeyDown += OnInputKeyDown;

        top.Add(historyView, _statusLabel, _inputField);

        // Focus the input immediately.
        _inputField.SetFocus();

        Application.Run();
        Application.Shutdown();
    }

    private async void OnInputKeyDown(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.Enter)
        {
            string text = _inputField!.Text?.ToString() ?? "";
            _inputField.Text = "";
            if (text.Length > 0)
            {
                await SendPromptAsync(text);
            }
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == (Key.CtrlMask | Key.O))
        {
            CycleCollapseMode();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.Esc)
        {
            _inputField!.Text = "";
            args.Handled = true;
        }
    }

    private async Task SendPromptAsync(string text)
    {
        if (_wsClient == null)
        {
            SetStatus("[not connected]");
        }
        else
        {
            try
            {
                await _wsClient.SendAsync(text);
            }
            catch (Exception ex)
            {
                SetStatus($"[send error] {ex.Message}");
            }
        }
    }

    private void CycleCollapseMode()
    {
        CollapseMode next = _model!.Mode switch
        {
            CollapseMode.Verbose   => CollapseMode.Minimized,
            CollapseMode.Minimized => CollapseMode.Quiet,
            _                      => CollapseMode.Verbose
        };
        _model.Mode = next;
        SetStatus($"View mode: {next}");
    }

    // Retries WebSocket connection until success or cancellation, with 200ms delays.
    private static async Task<ITransportClient> RetryConnectAsync(string url, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TransportClientWebsocket wsClient = new TransportClientWebsocket(url);
            try
            {
                await wsClient.ConnectAsync(cancellationToken);
                return wsClient;
            }
            catch (OperationCanceledException)
            {
                wsClient.Dispose();
                throw;
            }
            catch (Exception)
            {
                wsClient.Dispose();
                await Task.Delay(200, cancellationToken);
            }
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private void SetStatus(string text)
    {
        if (_nonInteractive)
        {
            Console.Error.WriteLine($"[status] {text}");
            return;
        }
        Application.MainLoop?.Invoke(() =>
        {
            if (_statusLabel != null)
                _statusLabel.Text = text;
        });
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
        _readCts?.Dispose();
        _wsClient?.Dispose();
        _docker?.Dispose();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        _readTask?.Wait(2000);
        _readCts?.Dispose();
        _wsClient?.Dispose();
        if (_containerId != null && _docker != null)
        {
            try { await _docker.StopAndRemoveContainerAsync(_containerId); } catch { }
        }
        _docker?.Dispose();
        _cts?.Dispose();
    }
}
