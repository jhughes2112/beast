using NAudio.Wave;
using System.Collections.Generic;
using TextCopy;


// Windows: the Win32 console for input (native mouse records, bracketed paste, VT keys), the Win32 clipboard
// for files and screenshots, TextCopy for clipboard text, and NAudio for sounds.
internal sealed class PlatformWindows : IPlatform
{
	public void EnterTerminalModes()
	{
		WindowsConsole.EnableVirtualTerminal();
	}

	public void ReapplyTerminalModes()
	{
		WindowsConsole.ReapplyModes();
	}

	public void RestoreTerminalModes()
	{
		WindowsConsole.Restore();
	}

	public ConsoleInputEvent? ReadInput(int timeoutMs)
	{
		return WindowsConsole.ReadInputWithTimeout(timeoutMs);
	}

	public void DiscardPendingInput()
	{
		WindowsConsole.DiscardPendingInput();
	}

	public int ReadCursorColumn(long deadline)
	{
		return WindowsConsole.ReadCursorColumn(deadline);
	}

	public string GetClipboardText()
	{
		return ClipboardService.GetText() ?? string.Empty;
	}

	public void SetClipboardText(string text)
	{
		ClipboardService.SetText(text);
	}

	public List<string> GetClipboardFiles()
	{
		return ClipboardMedia.GetFiles();
	}

	public string SaveClipboardImage(string folder)
	{
		return ClipboardMedia.SaveImage(folder);
	}

	// Plays the first existing candidate via NAudio, which supports WAV, MP3, and most other formats.
	// Playback is fire-and-forget on a threadpool thread; the player is disposed when playback stops.
	// Errors are silently swallowed — a missing or unplayable file is not fatal.
	public void PlaySound(IReadOnlyList<string> candidates)
	{
		string soundFile = SoundCandidates.FirstExisting(candidates);
		if (soundFile.Length == 0)
			return;
		try
		{
			AudioFileReader reader = new AudioFileReader(soundFile);
			WaveOutEvent    output = new WaveOutEvent();
			output.Init(reader);
			output.PlaybackStopped += (s, e) =>
			{
				output.Dispose();
				reader.Dispose();
			};
			output.Play();
		}
		catch { }
	}
}