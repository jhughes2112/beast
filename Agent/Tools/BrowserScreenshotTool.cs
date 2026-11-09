using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


// Backs the browser_screenshot tool: any page at one exact moment, the same bytes on every run. The browser
// side is Agent/Docker/browser_screenshot.js, which checks the steps and sizes; it runs in the model's own
// shell, so it has the shell user's rights and the sandbox's counted clock. Its picture goes under /tmp only,
// which lets roles with a read-only view of the workspace use it too.
public static class BrowserScreenshotTool
{
	private const string kScriptPath     = "/opt/agent-bins/browser_screenshot.js";
	private const string kOutputFolder   = "/tmp/";
	private const int    kTimeoutSeconds = 180;

	public static async Task<ToolResult> TakeAsync(string toolCallId, string page, string outputPath, string steps, string stepMs, int? width, int? height, CancellationToken ct)
	{
		ToolResult result;
		if (page.Length == 0)
		{
			result = new ToolResult(toolCallId, string.Empty, "browser_screenshot needs page: a file path or URL.", 1, 0);
		}
		else if (outputPath.Length == 0 || !Path.GetFullPath(outputPath).StartsWith(kOutputFolder, StringComparison.Ordinal))
		{
			result = new ToolResult(toolCallId, string.Empty, $"browser_screenshot's output_path must be under {kOutputFolder}, such as /tmp/work/view.png.", 1, 0);
		}
		else
		{
			// Without libfaketime: its frozen wall clock made one 400-step capture take 89 s instead of 3 (Nov 9).
			// The page needs no fake clock, since it runs on the browser's virtual time, and clockfs still dates the picture.
			string size = width.HasValue && height.HasValue ? $" {width.Value} {height.Value}" : string.Empty;
			result      = await ShellTools.BashAsync(toolCallId, $"env -u LD_PRELOAD node {kScriptPath} {Quote(page)} {Quote(Path.GetFullPath(outputPath))} {Quote(steps)} {Quote(stepMs)}{size}", kTimeoutSeconds, ct);
		}
		return result;
	}

	private static string Quote(string text)
	{
		return "'" + text.Replace("'", "'\\''") + "'";
	}
}