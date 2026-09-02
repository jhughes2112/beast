using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;


// Physical facts about a media file, read with ffprobe (in the container image alongside ffmpeg,
// and it reads still images as well as audio and video). Zero for anything ffprobe could not
// report, including when ffprobe itself is absent on a native debugging run — the caller then has
// only the byte count to go on.
public sealed class MediaProbe
{
	public int    Width           { get; }
	public int    Height          { get; }
	public double DurationSeconds { get; }

	private MediaProbe(int width, int height, double durationSeconds)
	{
		Width           = width;
		Height          = height;
		DurationSeconds = durationSeconds;
	}

	public static async Task<MediaProbe> ProbeAsync(string path, CancellationToken ct)
	{
		int    width    = 0;
		int    height   = 0;
		double duration = 0;

		try
		{
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName               = "ffprobe",
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true
			};
			psi.ArgumentList.Add("-v");
			psi.ArgumentList.Add("error");
			psi.ArgumentList.Add("-show_entries");
			psi.ArgumentList.Add("stream=width,height:format=duration");
			psi.ArgumentList.Add("-of");
			psi.ArgumentList.Add("default=noprint_wrappers=1");
			psi.ArgumentList.Add(path);

			using (Process process = new Process { StartInfo = psi })
			{
				process.Start();
				Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
				Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
				await process.WaitForExitAsync(ct);
				string output = await stdout;
				await stderr;

				foreach (string line in output.Split('\n'))
				{
					int eq = line.IndexOf('=');
					if (eq <= 0)
						continue;
					string key   = line.Substring(0, eq).Trim();
					string value = line.Substring(eq + 1).Trim();

					// The first video stream wins; ffprobe lists streams before the format block.
					if (key == "width" && width == 0)
						int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
					else if (key == "height" && height == 0)
						int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
					else if (key == "duration")
						double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// No ffprobe, or a file it cannot parse: the caller falls back to size on disk.
		}

		return new MediaProbe(width, height, duration);
	}
}