using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// inspect_media's max_dimension, through the real tool against real images: one past the limit is
// sent as a downscaled copy with its aspect ratio kept, asking again reuses that copy, and one
// within the limit goes as-is. Needs ffmpeg (in the container image beside ffprobe) to make the
// test images and to scale them, so it is skipped where there is none.
public static class MediaResizeTests
{
	private const string kModelId = "fake-vision";

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  MediaResizeTests");

		string folder = Path.Combine(Path.GetTempPath(), $"beast-resize-{Guid.NewGuid():N}");
		Directory.CreateDirectory(folder);
		string wide  = Path.Combine(folder, "wide.png");
		string small = Path.Combine(folder, "small.png");
		try
		{
			if (!await MakeImageAsync(wide, 3000, 1000) || !await MakeImageAsync(small, 400, 300))
			{
				ctx.Log("    SKIP: no ffmpeg to make or scale test images");
				return;
			}
			await TestDownscalesAndReusesAsync(ctx, wide, small);
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	private static async Task TestDownscalesAndReusesAsync(TestContext ctx, string wide, string small)
	{
		ModelConfig config = new ModelConfig { Id = kModelId, Name = "Fake Vision", ContextWindow = 200000, MaxOutputTokens = 4096, Input = new List<string> { "text", "image" } };
		LlmModel    model  = new LlmModel(kModelId, "http://localhost:1/v1/chat/completions", "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);

		LlmRegistry                  registry = new LlmRegistry();
		Dictionary<string, LlmModel> models   = new Dictionary<string, LlmModel>(StringComparer.OrdinalIgnoreCase);
		models[kModelId]                      = model;
		registry.RestoreModels(models);

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "resize", kModelId, "Tester",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test model.", transport, false);
		session.UpdateModel(model);

		MediaInspector inspector = new MediaInspector();
		ToolResult     first     = await inspector.InspectAsync("c1", new List<string> { wide, small }, 1000, string.Empty, false, null, registry, session, transport, 4096, CancellationToken.None);
		ctx.AssertEqual(0,    first.ExitCode, $"Resize: both files are sent ({first.StdErr})");
		ctx.AssertEqual(2, first.Media.Count, "Resize: one media file per path, in one result");
		if (first.Media.Count == 2)
		{
			string     scaled = first.Media[0].Path;
			MediaProbe probe  = await MediaProbe.ProbeAsync(scaled, CancellationToken.None);
			ctx.Assert(scaled != wide, "Resize: the wide image is sent as a copy, not the original");
			ctx.AssertEqual( 1000,         probe.Width, "Resize: its longer edge is cut to max_dimension");
			ctx.AssertEqual(  333,        probe.Height, "Resize: with the aspect ratio kept");
			ctx.AssertEqual(small, first.Media[1].Path, "Resize: an image within the limit goes as-is");
			ctx.AssertContains(first.StdOut, "downscaled from 3000x1000", "Resize: the result says what was shrunk");

			ToolResult again = await inspector.InspectAsync("c2", new List<string> { wide }, 1000, string.Empty, false, null, registry, session, transport, 4096, CancellationToken.None);
			ctx.Assert(again.Media.Count == 1 && again.Media[0].Path == scaled, "Resize: asking again reuses the same copy");
			File.Delete(scaled);
		}
	}

	// A solid-color PNG of the given size, made with ffmpeg; false when ffmpeg cannot run.
	private static async Task<bool> MakeImageAsync(string path, int width, int height)
	{
		bool made = false;
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo { FileName = "ffmpeg", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
			foreach (string arg in new string[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}", "-frames:v", "1", path })
				psi.ArgumentList.Add(arg);
			using Process process = new Process { StartInfo = psi };
			process.Start();
			Task<string> stdout = process.StandardOutput.ReadToEndAsync();
			Task<string> stderr = process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
			await stdout;
			await stderr;
			made = process.ExitCode == 0 && File.Exists(path);
		}
		catch (Exception)
		{
		}
		return made;
	}
}