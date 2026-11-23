using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// One Agent Client Protocol process: JSON-RPC over the child's stdin and stdout, one message per
// line. Everything the process says arrives on Events in order; Inject adds an event of the agent's
// own to the same queue, so a reader sees it in step with the process's messages.
public sealed class AcpConnection
{
	private readonly Process             _process;
	private readonly Channel<JsonObject> _events     = Channel.CreateUnbounded<JsonObject>();
	private readonly Queue<string>       _stderrTail = new Queue<string>();
	private readonly object              _writeLock  = new object();
	private          int                 _nextId;

	private AcpConnection(Process process)
	{
		_process = process;
	}

	public ChannelReader<JsonObject> Events
	{
		get
		{
			return _events.Reader;
		}
	}

	public bool HasExited
	{
		get
		{
			return _process.HasExited;
		}
	}

	public int Pid
	{
		get
		{
			return _process.Id;
		}
	}

	// Starts the process and begins reading it. Connection is null when it could not be started.
	public static (AcpConnection? Connection, string Error) Start(ProcessStartInfo psi)
	{
		AcpConnection? connection = null;
		string         error      = string.Empty;
		Process        process    = new Process { StartInfo = psi };
		bool           started    = false;
		try
		{
			started = process.Start();
		}
		catch (Win32Exception ex)
		{
			error = ex.Message;
		}

		if (started)
		{
			connection                 = new AcpConnection(process);
			process.ErrorDataReceived += (_, e) => connection.RememberStderr(e.Data);
			process.BeginErrorReadLine();
			_ = connection.PumpStdoutAsync();
		}
		else
		{
			process.Dispose();
			if (error.Length == 0)
			{
				error = "the process did not start";
			}
		}
		return (connection, error);
	}

	// Sends a request and returns the id its response will carry.
	public int SendRequest(string method, JsonObject parameters)
	{
		int id = Interlocked.Increment(ref _nextId);
		Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
		return id;
	}

	public void Send(JsonObject message)
	{
		lock (_writeLock)
		{
			_process.StandardInput.WriteLine(message.ToJsonString());
			_process.StandardInput.Flush();
		}
	}

	public void Inject(JsonObject evt)
	{
		_events.Writer.TryWrite(evt);
	}

	// Sends a request and waits for its response, passing over whatever else arrives meanwhile.
	// Result is null on failure: Code is the JSON-RPC error code, or 0 when no response came at all.
	public async Task<(JsonObject? Result, int Code, string Error)> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
	{
		JsonObject? result = null;
		int         code   = 0;
		string      error  = string.Empty;
		int         id     = SendRequest(method, parameters);

		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
		limit.CancelAfter(timeout);
		try
		{
			bool answered = false;
			while (!answered)
			{
				if (!await _events.Reader.WaitToReadAsync(limit.Token))
				{
					error    = $"the process exited: {StderrTail()}";
					answered = true;
				}
				else if (_events.Reader.TryRead(out JsonObject? message) && IsResponseTo(message, id))
				{
					answered = true;
					if (message["error"] is JsonObject failure)
					{
						code  = failure["code"] is JsonValue value && value.TryGetValue(out int number) ? number : 0;
						error = failure["message"]?.ToString() ?? "the request failed";
					}
					else
					{
						result = message["result"] as JsonObject ?? new JsonObject();
					}
				}
			}
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			error = $"no answer to {method} within {timeout.TotalSeconds:0}s";
		}
		return (result, code, error);
	}

	public static bool IsResponseTo(JsonObject message, int id)
	{
		return message["method"] == null && message["id"] is JsonValue value && value.TryGetValue(out int answered) && answered == id;
	}

	public string StderrTail()
	{
		lock (_stderrTail)
		{
			return _stderrTail.Count > 0 ? string.Join("\n", _stderrTail) : "(no output)";
		}
	}

	public void Kill()
	{
		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(true);
			}
		}
		catch (Exception)
		{
		}
		_process.Dispose();
		_events.Writer.TryComplete();
	}

	private async Task PumpStdoutAsync()
	{
		try
		{
			for (; ; )
			{
				string? line = await _process.StandardOutput.ReadLineAsync();
				if (line == null)
				{
					break;
				}

				JsonObject? message = null;
				try
				{
					message = JsonNode.Parse(line) as JsonObject;
				}
				catch (JsonException)
				{
					RememberStderr(line);
				}
				if (message != null)
				{
					_events.Writer.TryWrite(message);
				}
			}
		}
		catch (Exception ex)
		{
			RememberStderr($"stdout read failed: {ex.Message}");
		}
		_events.Writer.TryComplete();
	}

	private void RememberStderr(string? line)
	{
		if (!string.IsNullOrWhiteSpace(line))
		{
			lock (_stderrTail)
			{
				_stderrTail.Enqueue(line);
				while (_stderrTail.Count > 20)
				{
					_stderrTail.Dequeue();
				}
			}
		}
	}
}