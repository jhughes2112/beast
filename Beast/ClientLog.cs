using System;

// Centralised logger for Beast. Verbose messages are only emitted when the verbose flag is set.
public class ClientLog
{
	private readonly bool _verbose;

	public ClientLog(bool verbose)
	{
		_verbose = verbose;
	}

	// Emits a message only when verbose mode is enabled.
	public void Verbose(string message)
	{
		if (_verbose)
			Console.Error.WriteLine(message);
	}

	// Always emits an error message to stderr.
	public void Error(string message)
	{
		Console.Error.WriteLine(message);
	}
}