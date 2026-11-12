using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;


// A scripted model driving the REAL tools (bash, write_file, edit_file, read_file, ls) through the
// production turn loop, in the order a model actually uses them: shell makes a file, file tools
// rewrite it, shell changes it again, and so on — every hand-off between the shell user and the root
// agent. Run it with the working directory on a Windows bind mount (see the container check), since
// that is where the sandbox's ownership rules and the kernel's protected_regular rule actually bite;
// in the container's own /tmp, root writes behave differently and hide the failures this exists for.
// Needs bash, so it is skipped on Windows.
public static class ToolSandboxTests
{
	private static readonly string[] ToolNames = new string[] { "bash", "readonly_bash", "write_file", "edit_file", "read_file", "ls" };

	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  ToolSandboxTests");
		if (!OperatingSystem.IsLinux())
		{
			ctx.Log("    SKIP: needs bash (runs in the container)");
			return;
		}

		FakeProvider? server = FakeProvider.StartOnRandomPort();
		if (server == null)
		{
			ctx.Log("    SKIP: no free port for the fake provider");
			return;
		}

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		Task   serverTask                 = server.RunAsync(cts.Token);
		string previous                   = Environment.CurrentDirectory;
		string root                       = Path.Combine(previous, $"beast-tooltest-{Guid.NewGuid():N}");

		// As in a real run, the session's counters stay put in a state folder while the working folder moves.
		string state                      = Path.Combine(previous, $"beast-toolstate-{Guid.NewGuid():N}");

		try
		{
			AgentState.Initialize(state);
			Directory.CreateDirectory(Path.Combine(root, ".github"));
			Directory.CreateDirectory(Path.Combine(root, ".beast"));
			File.WriteAllText(Path.Combine(root, ".github", "ci.yml"), "on: push\n");
			File.WriteAllText(Path.Combine(root, ".beast", "secret.txt"), "TOP-SECRET\n");
			File.WriteAllText(Path.Combine(root, "README.md"), "hello\n");

			Directory.SetCurrentDirectory(root);
			Sandbox.Initialize(root);
			string lockdown = await Sandbox.LockDownAsync(cts.Token);
			ctx.Log($"    lockdown: {lockdown}");

			await RunScriptAsync(ctx, server, root, cts.Token);
			await FetchFoldersAsync(ctx, cts.Token);
			await FetchLiveAsync(ctx, cts.Token);
		}
		finally
		{
			Directory.SetCurrentDirectory(previous);
			Sandbox.Initialize(previous);
			AgentState.Initialize(string.Empty);
			// As root: the lockdown left parts of the tree root-owned and read-only.
			await ShellTools.AgentBashAsync("cleanup", $"rm -rf '{root}' '{state}'", null, CancellationToken.None);
			cts.Cancel();
			try
			{
				await serverTask;
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private static string FileTime(string path, CancellationToken ct)
	{
		return ShellTools.AgentBashAsync("clock_stat", $"stat -c %Y '{path}'", null, ct).GetAwaiter().GetResult().StdOut.Trim();
	}

	// A seeded run: what changes takes the counted time, what was already there keeps its own.
	// A seeded round runs its calls one at a time in call order, and every call still gets its result. Runs while the
	// counted clock is on, since a seeded round advances it.
	private static async Task SeededRoundsAsync(TestContext ctx, CancellationToken ct)
	{
		List<string> events = new List<string>();
		void Note(string text)
		{
			lock (events)
				events.Add(text);
		}
		Tool Step(string name, bool fails)
		{
			return new Tool
			{
				Definition = new ToolDefinition { Type = "function", Function = new FunctionDefinition { Name = name, Description = name, Parameters = new System.Text.Json.Nodes.JsonObject() } },
				Handler = async (args, id, toolCt, transport, sessionId, maxOutputTokens) =>
				{
					Note(name + "+");
					await Task.Delay(200, toolCt);
					Note(name + "-");
					if (fails)
						throw new InvalidOperationException("boom");
					return new ToolResult(id, "done", string.Empty, 0, 1);
				}
			};
		}
		TestCaptureTransport transport = new TestCaptureTransport();
		Session              session   = new Session(new BeastSession("p", "p", "m", "r", string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true), string.Empty, transport, false);
		Tool[]               tools     = new Tool[] { Step("a", true), Step("b", false), Step("c", false) };
		ProtocolCallPayload Round()
		{
			List<SemanticToolCall> calls = new List<SemanticToolCall>();
			foreach (string name in new[] { "a", "b", "c" })
				calls.Add(new SemanticToolCall { Id = name, Name = name, ArgumentsJson = "{}" });
			return new ProtocolCallPayload(string.Empty, string.Empty, calls, new List<ToolResult>(), "tool_calls", new TokenUsageInfo(), 0m);
		}

		ProtocolCallPayload seeded = Round();
		await ToolDispatch.DispatchAsync(seeded, tools, session, transport, ct, true);
		ctx.AssertEqual("a+,a-,b+,b-,c+,c-", string.Join(",", events), "Seeded round: calls run one at a time in call order, even after one fails");
		ctx.AssertEqual("a,b,c", string.Join(",", seeded.ToolResults.ConvertAll(r => r.Id)), "Seeded round: every call has its result, in call order");

		// Unseeded rounds run the same way, so a later call finds what an earlier one made: the field case
		// was a screenshot and the inspect of its file in one turn, started together.
		events.Clear();
		await ToolDispatch.DispatchAsync(Round(), tools, session, transport, ct);
		ctx.AssertEqual("a+,a-,b+,b-,c+,c-", string.Join(",", events), "Unseeded round: calls run one at a time in call order too");

		ProtocolCallPayload interrupted = Round();
		using CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
		try
		{
			await ToolDispatch.DispatchAsync(interrupted, tools, session, transport, stop.Token, true);
		}
		catch (OperationCanceledException)
		{
		}
		ctx.AssertEqual(3, interrupted.ToolResults.Count, "Seeded round: an interrupted round still answers every call");
	}

	// Where fetch_url saves a page: numbered folders in call order, whatever the page says, that the shell can write in.
	private static async Task FetchFoldersAsync(TestContext ctx, CancellationToken ct)
	{
		string Reserve() => (string)Reflect.Static(typeof(WebFetch), "ReserveFolder", Type.EmptyTypes, Array.Empty<object>())!;
		int Number(string folder) => int.Parse(Path.GetFileName(folder).Substring("fetch_".Length));
		string Save(string folder, string page) => (string)Reflect.Static(typeof(WebFetch), "SaveFiles",
			new Type[] { typeof(string), typeof(string), typeof(string), typeof(byte[]), typeof(string) },
			new object[] { folder, "http://example.test/page", "text/html", System.Text.Encoding.UTF8.GetBytes(page), null! })!;

		// Numbers already on disk are skipped, so an earlier run's folders would leave gaps.
		await ShellTools.AgentBashAsync("fetch_clear", "rm -rf /tmp/work/fetch_*", null, ct);

		string first  = Reserve();
		string second = Reserve();
		ctx.Assert(first.StartsWith("/tmp/work/fetch_"), $"Fetch: a page goes under /tmp/work (got {first})");
		ctx.AssertEqual(Number(first) + 1, Number(second), "Fetch: folders are numbered in the order calls start");

		string list1 = Save(first, "<p>Visitors: 1</p>\n<p>Thanks</p>\n");
		string list2 = Save(first, "<p>Visitors: 2</p>\n<p>Thanks</p>\n");
		ctx.AssertEqual(list1, list2, "Fetch: a page that changes between visits but stays the same size lists the same files");
		ctx.Assert(list1.Contains($"- {first}/page.html  (33 bytes, 2 lines): the exact bytes", StringComparison.Ordinal), $"Fetch: the listing names the saved page by its real path, with its size and lines — {list1}");
		ctx.Assert(list1.Contains(first + "/text.txt", StringComparison.Ordinal) && list1.Contains(first + "/outline.html", StringComparison.Ordinal), $"Fetch: and the page's text and outline beside it — {list1}");
		ctx.AssertEqual("Visitors: 2 Thanks", File.ReadAllText(first + "/text.txt"), "Fetch: the text view is the page's words");

		ToolResult wrote = await ShellTools.BashAsync("fetch_write", $"echo text > '{first}/extracted.txt' && id -un", null, ct);
		ctx.AssertEqual(0, wrote.ExitCode, $"Fetch: the shell can write extracted text beside the page ({wrote.StdErr})");
		ctx.AssertEqual("beast", wrote.StdOut.Trim(), "Fetch: and that shell is the shell user, not root");

		Directory.CreateDirectory($"/tmp/work/fetch_{Number(second) + 1}");
		ctx.AssertEqual(Number(second) + 2, Number(Reserve()), "Fetch: a number already on disk is skipped, as after a restart");

		string planted = Reserve();
		string target  = Path.Combine(Path.GetTempPath(), $"beast-fetchlink-{Guid.NewGuid():N}");
		Directory.CreateDirectory(target);
		Directory.CreateSymbolicLink(planted, target);
		Save(planted, "<p>page</p>");
		ctx.Assert(!File.Exists(Path.Combine(target, "text.txt")), "Fetch: a link left at the reserved path does not redirect root's writes");
		ctx.Assert(new FileInfo(planted).LinkTarget == null && File.Exists(Path.Combine(planted, "text.txt")), "Fetch: the page lands in a real folder instead");
		Directory.Delete(target, true);
	}

	// fetch_url end to end against a local server: each kind of resource is saved with its views made in code and
	// listed, and nothing else happens. A PDF gets its text, an archive is named for what it is, an error is an error.
	private static async Task FetchLiveAsync(TestContext ctx, CancellationToken ct)
	{
		string pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
			+ "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 100]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n"
			+ "4 0 obj<</Length 39>>stream\nBT /F1 18 Tf 20 40 Td (Hello PDF) Tj ET\nendstream endobj\n"
			+ "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n";
		using MemoryStream zip = new MemoryStream();
		using (System.IO.Compression.ZipArchive archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, true))
		{
			using StreamWriter entry = new StreamWriter(archive.CreateEntry("inside.txt").Open());
			entry.Write("zipped words");
		}
		Dictionary<string, (string Type, byte[] Body)> served = new Dictionary<string, (string, byte[])>
		{
			["/docs/page.html"]   = ("text/html", System.Text.Encoding.UTF8.GetBytes("<html><head><style>p{}</style></head><body><h1>Title</h1><p>Some &amp; words</p><script>x()</script></body></html>")),
			["/files/report.pdf"] = ("application/pdf", System.Text.Encoding.ASCII.GetBytes(pdf)),
			["/files/bundle.zip"] = ("application/zip", zip.ToArray()),
		};

		System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using System.Net.HttpListener listener = new System.Net.HttpListener();
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();
		Task serving = Task.Run(async () =>
		{
			while (listener.IsListening)
			{
				System.Net.HttpListenerContext request;
				try
				{ request = await listener.GetContextAsync(); }
				catch { break; }
				if (served.TryGetValue(request.Request.Url!.AbsolutePath, out (string Type, byte[] Body) file))
				{
					request.Response.ContentType = file.Type;
					await request.Response.OutputStream.WriteAsync(file.Body, ct);
				}
				else
				{
					request.Response.StatusCode = 404;
				}
				request.Response.Close();
			}
		}, ct);

		string     at   = $"http://127.0.0.1:{port}";
		ToolResult page = await WebFetch.FetchAsync("live1", $"{at}/docs/page.html", ct);
		ctx.Assert(page.ExitCode == 0 && page.StdOut.Contains("HTTP 200, text/html", StringComparison.Ordinal) && page.StdOut.Contains("/page.html  (", StringComparison.Ordinal)
			&& page.StdOut.Contains("/text.txt  (", StringComparison.Ordinal) && page.StdOut.Contains("/outline.html  (", StringComparison.Ordinal) && !page.StdOut.Contains("read_file", StringComparison.Ordinal),
			$"Fetch: a page is saved with its text and outline, and listed — {page.StdOut} {page.StdErr}");
		string folder = page.StdOut.Contains("Saved in ", StringComparison.Ordinal) ? page.StdOut.Split("Saved in ")[1].Split(':')[0] : string.Empty;
		ctx.AssertEqual("Title Some & words", folder.Length > 0 && File.Exists($"{folder}/text.txt") ? File.ReadAllText($"{folder}/text.txt") : string.Empty, "Fetch: the text has no tags, scripts or styles, and its entities are decoded");

		ToolResult report = await WebFetch.FetchAsync("live2", $"{at}/files/report.pdf", ct);
		string     pdfDir = report.StdOut.Contains("Saved in ", StringComparison.Ordinal) ? report.StdOut.Split("Saved in ")[1].Split(':')[0] : string.Empty;
		ctx.Assert(report.ExitCode == 0 && report.StdOut.Contains("the PDF's text", StringComparison.Ordinal) && File.Exists($"{pdfDir}/text.txt") && File.ReadAllText($"{pdfDir}/text.txt").Contains("Hello PDF", StringComparison.Ordinal),
			$"Fetch: a PDF gets its text extracted — {report.StdOut} {report.StdErr}");

		ToolResult bundle = await WebFetch.FetchAsync("live3", $"{at}/files/bundle.zip", ct);
		ctx.Assert(bundle.ExitCode == 0 && bundle.StdOut.Contains("/bundle.zip  (", StringComparison.Ordinal) && bundle.StdOut.Contains("It is binary: Zip archive", StringComparison.Ordinal),
			$"Fetch: an archive is saved and named for what it is — {bundle.StdOut} {bundle.StdErr}");

		ToolResult missing = await WebFetch.FetchAsync("live4", $"{at}/nothing", ct);
		ctx.Assert(missing.ExitCode != 0 && missing.StdErr.Contains("HTTP 404", StringComparison.Ordinal), $"Fetch: a missing page is an error — {missing.StdErr}");

		ToolResult notWeb = await WebFetch.FetchAsync("live5", "file:///etc/passwd", ct);
		ctx.Assert(notWeb.ExitCode != 0 && notWeb.StdErr.Contains("http", StringComparison.Ordinal), $"Fetch: only http and https — {notWeb.StdErr}");

		listener.Stop();
		await serving;
	}

	private static async Task FileClockAsync(TestContext ctx, string root, CancellationToken ct)
	{
		string parent = Path.GetDirectoryName(root)!;
		await ShellTools.AgentBashAsync("clock", $"cd '{root}' && echo old > clock_old.txt && touch -d @1000000000 clock_old.txt", null, ct);

		ctx.AssertEqual(string.Empty, await Sandbox.AdvanceFileClockAsync(1, ct), "File clock: it starts");
		long first = Sandbox.FileClockNow;
		ctx.AssertEqual((first - 60).ToString(), FileTime(parent, ct), "File clock: the first round stamps the workspace's parent with the date itself");

		// Read within the same call, which stamping after the fact could never cover.
		ToolResult same  = await ShellTools.BashAsync("clock1", "echo a > clock_bash.txt; stat -c %Y clock_bash.txt; date +%s; touch clock_touch.txt; stat -c %Y clock_touch.txt; ls", null, ct);
		string[]   lines = same.StdOut.Split('\n');
		ctx.Assert(same.ExitCode == 0, $"File clock: bash runs under it — exit {same.ExitCode}: {same.StdErr}");
		ctx.AssertEqual(first.ToString(), lines.Length > 0 ? lines[0].Trim() : string.Empty, "File clock: a file reads as counted in the call that made it");
		ctx.AssertEqual(first.ToString(), lines.Length > 1 ? lines[1].Trim() : string.Empty, "File clock: date in the shell reads the counted time");
		ctx.AssertEqual(first.ToString(), lines.Length > 2 ? lines[2].Trim() : string.Empty, "File clock: a plain touch takes the counted time");

		ctx.AssertEqual(string.Empty, await Sandbox.AdvanceFileClockAsync(2, ct), "File clock: it keeps counting");
		long second = Sandbox.FileClockNow;
		ctx.AssertEqual(120L, second - first, "File clock: each tool call counts one minute");
		await FileTools.WriteFileAsync("clock2", "clock_write.txt", "b\n", ct);
		await FileTools.EditFileAsync("clock3", "clock_bash.txt", "a", "edited", ct);
		ctx.AssertEqual(second.ToString(), FileTime(Path.Combine(root, "clock_write.txt"), ct), "File clock: write_file takes the round's time");
		ctx.AssertEqual(second.ToString(), FileTime(Path.Combine(root, "clock_bash.txt"), ct), "File clock: edit_file takes the round's time");
		ctx.AssertEqual(second.ToString(), FileTime(root, ct), "File clock: the workspace folder follows its latest change");
		ctx.AssertEqual("1000000000", FileTime(Path.Combine(root, "clock_old.txt"), ct), "File clock: a file from before the clock started keeps its time");

		// The temp folder too, including a socket, and including what was there before the clock started.
		string tempFile = Path.Combine(Path.GetTempPath(), $"clock-{Guid.NewGuid():N}");
		await FileTools.WriteFileAsync("clock5", tempFile, "c\n", ct);
		ctx.AssertEqual(second.ToString(), FileTime(tempFile, ct), "File clock: write_file in the temp folder takes the round's time");
		ToolResult temp = await ShellTools.BashAsync("clock6",
			$"echo d > '{tempFile}.sh'; stat -c %Y '{tempFile}.sh' /tmp/..; python3 -c \"import socket; s=socket.socket(socket.AF_UNIX); s.bind('{tempFile}.sock'); s.listen(1); c=socket.socket(socket.AF_UNIX); c.connect('{tempFile}.sock'); print('socket ok')\"; rm -f '{tempFile}' '{tempFile}.sh' '{tempFile}.sock'", null, ct);
		ctx.Assert(temp.StdOut.StartsWith(second.ToString(), StringComparison.Ordinal) && temp.StdOut.Contains("socket ok", StringComparison.Ordinal), $"File clock: the shell's temp files are counted and sockets work there — {temp.StdOut} {temp.StdErr}");

		ToolResult caps = await ShellTools.BashAsync("clock4", "grep CapBnd /proc/self/status", null, ct);
		bool       read = long.TryParse(caps.StdOut.Replace("CapBnd:", string.Empty).Trim(), System.Globalization.NumberStyles.HexNumber, null, out long bnd);
		ctx.Assert(read && (bnd & (1L << 21)) == 0, $"File clock: the shell cannot hold SYS_ADMIN — {caps.StdOut} {caps.StdErr}");

		// A server's Date header reads the counted time; nothing else that looks like one changes.
		const string realDate = "Fri, 06 Nov 2026 16:39:45 GMT";
		string     countedDate = DateTimeOffset.FromUnixTimeSeconds(second).ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);
		ToolResult headers     = await ShellTools.BashAsync("clock7",
			$"printf 'HTTP/2 200 \\r\\ndate: {realDate}\\r\\nDate: {realDate}\\r\\nlast-modified: {realDate}\\r\\nupdate: {realDate}\\r\\n'; printf '< date: {realDate}\\n' >&2", null, ct);
		ctx.Assert(headers.StdOut.Contains($"\ndate: {countedDate}\r\nDate: {countedDate}\r\n", StringComparison.Ordinal), $"File clock: a Date header reads the counted time, in either case — {headers.StdOut}");
		ctx.Assert(headers.StdOut.Contains($"last-modified: {realDate}", StringComparison.Ordinal) && headers.StdOut.Contains($"update: {realDate}", StringComparison.Ordinal), $"File clock: other lines with a date are left alone — {headers.StdOut}");
		ctx.Assert(headers.StdErr.Contains($"< date: {countedDate}", StringComparison.Ordinal), $"File clock: curl -v's form on stderr too — {headers.StdErr}");

		// A CDN's per-request headers go, in any curl form; the same words outside a header block stay.
		const string cdnHeaders = "HTTP/2 200 \r\ncontent-type: text/javascript\r\nage: 1487966\r\ncf-cache-status: HIT\r\nserver: cloudflare\r\ncf-ray: a56d95b4ae6f6b73-DFW\r\n\r\nage: 30\n";
		ctx.AssertEqual("HTTP/2 200 \r\ncontent-type: text/javascript\r\nserver: cloudflare\r\n\r\nage: 30\n", Sandbox.SteadyShellOutput(cdnHeaders), "File clock: per-request headers are removed from a response");
		ctx.AssertEqual("< HTTP/2 200\n< server: cloudflare\n", Sandbox.SteadyShellOutput("< HTTP/2 200\n< AGE: 5\n< server: cloudflare\n< cf-ray: abc\n"), "File clock: curl -v's headers too, in either case");
		ctx.AssertEqual("name: walrus\nage: 30\ncf-ray: mine\n", Sandbox.SteadyShellOutput("name: walrus\nage: 30\ncf-ray: mine\n"), "File clock: output without a response keeps those words");

		// Real `ls -l` output, in every date style, on a file whose real time is years old. Only the date changes.
		System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
		DateTimeOffset counted = DateTimeOffset.FromUnixTimeSeconds(second);
		string lsPlain = $"{counted.ToString("MMM", invariant)} {counted.Day,2} {counted.ToString("HH:mm", invariant)} clock_old.txt";
		string lsIso   = $"{counted.ToString("yyyy-MM-dd HH:mm", invariant)} clock_old.txt";
		string lsFull  = $"{counted.ToString("yyyy-MM-dd HH:mm:ss", invariant)}.000000000 +0000 clock_old.txt";
		ToolResult listed = await ShellTools.BashAsync("clock8", "ls -l clock_old.txt; ls -lh clock_old.txt; ls -l --time-style=long-iso clock_old.txt; ls -l --full-time clock_old.txt; echo 'Sep  9 2001 is not a listing'", null, ct);
		ctx.Assert(!listed.StdOut.Contains("2001-09", StringComparison.Ordinal) && !listed.StdOut.Contains("Sep  9  2001", StringComparison.Ordinal), $"File clock: no ls -l style shows the real date — {listed.StdOut}");
		ctx.Assert(listed.StdOut.Contains(lsPlain, StringComparison.Ordinal) && listed.StdOut.Contains(lsIso, StringComparison.Ordinal) && listed.StdOut.Contains(lsFull, StringComparison.Ordinal), $"File clock: ls -l reads the counted time in its own format — {listed.StdOut}");
		ctx.Assert(listed.StdOut.Contains("Sep  9 2001 is not a listing", StringComparison.Ordinal), $"File clock: a date outside a listing is left alone — {listed.StdOut}");
		ToolResult readonlyListed = await ShellTools.ReadonlyBashAsync("clock9", "ls -l clock_old.txt", null, ct);
		ctx.Assert(readonlyListed.StdOut.Contains(lsPlain, StringComparison.Ordinal), $"File clock: readonly_bash too — {readonlyListed.StdOut}");
		ctx.AssertEqual("1000000000", FileTime(Path.Combine(root, "clock_old.txt"), ct), "File clock: rewriting a listing leaves the file's real time alone");

		// Chromium's graphics context address reads the same on every run; other hex is left alone.
		const string glMessage = "gl_utils.cc:430] [.WebGL-0x3db400156600]GL Driver Message";
		ToolResult tagged = await ShellTools.BashAsync("clock10", $"echo '{glMessage}'; echo 'sha 0x3db400156600'; echo '[.WebGPU-0x36f400155a00]x' >&2", null, ct);
		ctx.Assert(tagged.StdOut.Contains("gl_utils.cc:430] [.WebGL-0x0]GL Driver Message", StringComparison.Ordinal) && tagged.StdOut.Contains("sha 0x3db400156600", StringComparison.Ordinal), $"File clock: a WebGL context's address is fixed and other hex stays — {tagged.StdOut}");
		ctx.Assert(tagged.StdErr.Contains("[.WebGPU-0x0]x", StringComparison.Ordinal), $"File clock: so is a WebGPU context's, on stderr — {tagged.StdErr}");

		// libfaketime's warnings name its semaphore after a process id, which any earlier process in the container moves on.
		const string semWarning = "libfaketime: sem_name was /faketime_sem_63, created locally: false";
		ctx.AssertEqual("libfaketime: sem_name was /faketime_sem, created locally: false", Sandbox.SteadyShellOutput(semWarning), "File clock: libfaketime's semaphore name loses its process id");
		ctx.AssertEqual(Sandbox.SteadyShellOutput(semWarning), Sandbox.SteadyShellOutput(semWarning.Replace("_63", "_65")), "File clock: so runs a few processes apart read the same");
		ctx.AssertEqual("shm /faketime_shm and faketime_semaphore_7", Sandbox.SteadyShellOutput("shm /faketime_shm_4021 and faketime_semaphore_7"), "File clock: its shared memory's too, and nothing else");

		string shotFolder = await ScreenshotAsync(ctx, ct);
		await SeededRoundsAsync(ctx, ct);

		// A clock starting fresh saves where it is. An earlier test may have left one running, so this one starts its own.
		Directory.SetCurrentDirectory(parent);
		await Sandbox.StopFileClockAsync(ct);
		Directory.SetCurrentDirectory(root);
		await Sandbox.AdvanceFileClockAsync(3, ct);
		long countedNow   = Sandbox.FileClockNow;
		long savedBase    = SessionService.GetCounter(Sandbox.kClockBaseCounter);
		long savedMinutes = SessionService.GetCounter(Sandbox.kClockMinutesCounter);
		ctx.AssertEqual(3L, savedMinutes, "File clock: its tool call count is saved");
		ctx.AssertEqual(countedNow, savedBase + savedMinutes * 60, "File clock: and its date");

		// A restart loses the mount and the clock in memory; a resumed session carries on from the saved time.
		Directory.SetCurrentDirectory(parent);
		await Sandbox.StopFileClockAsync(ct);
		Directory.SetCurrentDirectory(root);
		SessionService.SetCounter(Sandbox.kClockBaseCounter, savedBase);
		SessionService.SetCounter(Sandbox.kClockMinutesCounter, savedMinutes);
		await Sandbox.AdvanceFileClockAsync(1, ct);
		ctx.AssertEqual(countedNow + 60, Sandbox.FileClockNow, "File clock: a resumed session's time carries on");

		// Off the mount, so the real files can be checked and the test folder removed.
		Directory.SetCurrentDirectory(parent);
		await Sandbox.StopFileClockAsync(ct);
		Directory.SetCurrentDirectory(root);
		await ScreenshotWithoutSeedAsync(ctx, shotFolder, ct);
		ctx.AssertEqual($"date: {realDate}", Sandbox.SteadyShellOutput($"date: {realDate}"), "File clock: without a counted clock a Date header stays as it is");
		ctx.AssertEqual("-rw-r--r-- 1 a b 2 Sep  9  2001 f", Sandbox.SteadyShellOutput("-rw-r--r-- 1 a b 2 Sep  9  2001 f"), "File clock: without a counted clock a listing stays as it is");
		ctx.AssertEqual(glMessage, Sandbox.SteadyShellOutput(glMessage), "File clock: without a counted clock a context's address stays as it is");
		ctx.AssertEqual(semWarning, Sandbox.SteadyShellOutput(semWarning), "File clock: without a counted clock libfaketime's names stay as they are");
		ctx.AssertEqual(cdnHeaders, Sandbox.SteadyShellOutput(cdnHeaders), "File clock: without a counted clock a response's headers stay as they are");
		ctx.AssertEqual(second.ToString(), FileTime(Path.Combine(root, "clock_write.txt"), ct), "File clock: the times are on the real files");
	}

	// The real command and browser, under the counted clock. The page paints from everything that could differ
	// between runs, and has a CSS animation and transition, so any of them moving changes the picture.
	private const string kShotPage = """
		<!doctype html><style>
		#a { position: absolute; top: 70px; left: 0; width: 40px; height: 40px; background: red; animation: go 2s linear infinite alternate; }
		#b { position: absolute; top: 120px; left: 0; width: 40px; height: 40px; background: blue; transition: left 1s; }
		#b.on { left: 200px; }
		@keyframes go { from { left: 0; } to { left: 200px; } }
		</style><canvas id=c width=64 height=64></canvas><div id=a></div><div id=b></div><script>
		const paint = document.getElementById('c').getContext('2d');
		let frames = 0, ticks = 0, stamp = 0;
		setInterval(() => ticks++, 50);
		setTimeout(() => document.getElementById('b').className = 'on', 100);
		(function tick(time) {
			stamp = time || 0;
			frames++;
			const n = (frames * 3 + ticks * 11 + stamp + new Date().getTime() % 251 + performance.now() + document.timeline.currentTime + Math.random() * 5) % 256;
			paint.fillStyle = `rgb(${Math.floor(n)},${Math.floor(n * 3) % 256},${Math.floor(n * 5) % 256})`;
			paint.fillRect(0, 0, 64, 64);
			requestAnimationFrame(tick);
		})();
		</script>
		""";

	// With no seed, anything goes: the same calls that a seeded run refuses all take their pictures.
	private static async Task ScreenshotWithoutSeedAsync(TestContext ctx, string folder, CancellationToken ct)
	{
		string     flagFile = $"{folder}/flag.png";
		ToolResult flag     = await ShellTools.BashAsync("flag", $"chromium --headless --screenshot={flagFile} file://$PWD/shot_page.html", null, ct);
		ctx.Assert(File.Exists(flagFile), $"Screenshot: without a seed, --screenshot works — {flag.StdOut} {flag.StdErr}");
		ToolResult flagReadonly = await ShellTools.ReadonlyBashAsync("flag2", $"chromium --headless --screenshot={folder}/flag2.png file://$PWD/shot_page.html", null, ct);
		ctx.Assert(File.Exists($"{folder}/flag2.png"), $"Screenshot: so does readonly_bash's — {flagReadonly.StdOut} {flagReadonly.StdErr}");

		foreach ((string name, string command, int captures) in new[] { ("Node Playwright", "node shot_lib.js playwright", 3), ("Puppeteer", "node shot_lib.js puppeteer", 2), ("Python Playwright", "python3 shot_lib.py", 3) })
		{
			ToolResult library = await ShellTools.BashAsync("library", command, null, ct);
			ctx.AssertEqual(captures, library.StdOut.Split("captured").Length - 1, $"Screenshot: without a seed, {name}'s screenshot calls all work — {library.StdOut} {library.StdErr}");
		}

		ToolResult tool = await BrowserScreenshotTool.TakeAsync("tool", "shot_page.html", $"{folder}/tool.png", "5", "10", null, null, ct);
		ctx.Assert(tool.ExitCode == 0 && File.Exists($"{folder}/tool.png"), $"Screenshot: without a seed, the tool works too — {tool.StdOut} {tool.StdErr}");

		await ShellTools.AgentBashAsync("shot_cleanup", $"rm -rf '{folder}' shot_page.html shot_broken.html shot_shader.html shot_lib.js shot_lib.py", null, ct);
	}

	private static async Task<string> ScreenshotAsync(TestContext ctx, CancellationToken ct)
	{
		File.WriteAllText("shot_page.html", kShotPage);
		File.WriteAllText("shot_broken.html", "<!doctype html><h1>Words</h1><img src='shot_gone.png'><script>console.log('shot note'); throw new Error('shot boom')</script>");
		string folder = $"/tmp/beast-shot-{Guid.NewGuid():N}";

		async Task<(ToolResult Result, string Hash)> Shot(string name, string steps, string stepMs)
		{
			string     output = $"{folder}/{name}.png";
			ToolResult result = await BrowserScreenshotTool.TakeAsync(name, "shot_page.html", output, steps, stepMs, 320, 200, ct);
			return (result, File.Exists(output) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output))) : string.Empty);
		}

		(ToolResult first, string firstHash) = await Shot("first", "30", "16.7");
		ctx.Assert(first.ExitCode == 0 && firstHash.Length > 0 && first.StdOut.Contains("30 steps of 16.7 ms, page time 501 ms", StringComparison.Ordinal), $"Screenshot: it captures the page and says how much time passed — {first.StdOut} {first.StdErr}");
		bool same = true;
		for (int i = 0; i < 5; i++)
			same = same && (await Shot($"again{i}", "30", "16.7")).Hash == firstHash;
		ctx.Assert(same, "Screenshot: the same steps give the same bytes, CSS animation, timers, clocks and random numbers included");
		ctx.Assert((await Shot("later", "31", "16.7")).Hash != firstHash, "Screenshot: one more step is another moment");
		ctx.Assert((await Shot("longer", "30", "16.8")).Hash != firstHash, "Screenshot: a longer step is another moment");

		ToolResult broken = await BrowserScreenshotTool.TakeAsync("broken", "shot_broken.html", $"{folder}/broken.png", "5", "10", null, null, ct);
		ctx.Assert(broken.ExitCode == 0 && broken.StdOut.Contains("shot boom", StringComparison.Ordinal) && broken.StdOut.Contains("shot note", StringComparison.Ordinal) && broken.StdOut.Contains("shot_gone.png", StringComparison.Ordinal), $"Screenshot: a plain page is captured, with its errors, console messages and failed files listed — {broken.StdOut} {broken.StdErr}");
		// three.js logs a shader that fails to compile in full, numbered: only the error and the lines around it come back.
		File.WriteAllText("shot_shader.html", "<!doctype html><script>const src = []; for (let i = 1; i <= 1400; i++) src.push(i + ': float line' + i + ' = 0.0;'); console.error('THREE.WebGLProgram: shader error\\nERROR: 0:1201: \\'getShadowMask\\' : no matching overloaded function found\\n' + src.join('\\n'));</script>");
		ToolResult shader = await BrowserScreenshotTool.TakeAsync("shader", "shot_shader.html", $"{folder}/shader.png", "2", "10", null, null, ct);
		ctx.Assert(shader.ExitCode == 0 && shader.StdOut.Contains("ERROR: 0:1201", StringComparison.Ordinal) && shader.StdOut.Contains("1201: float line1201", StringComparison.Ordinal) && shader.StdOut.Contains("1197: float", StringComparison.Ordinal) && !shader.StdOut.Contains("1196: float", StringComparison.Ordinal) && !shader.StdOut.Contains("700: float", StringComparison.Ordinal) && shader.StdOut.Length < 3000,
			$"Screenshot: a shader error keeps the error and the source lines around it, not the whole shader ({shader.StdOut.Length} characters) — {shader.StdOut[..Math.Min(600, shader.StdOut.Length)]}");
		ToolResult missing = await BrowserScreenshotTool.TakeAsync("missing", "shot_gone.html", $"{folder}/gone.png", "1", "10", null, null, ct);
		ctx.Assert(missing.ExitCode != 0 && missing.StdErr.Contains("no file at", StringComparison.Ordinal), $"Screenshot: a missing page says so — {missing.StdErr}");
		ToolResult rough = await BrowserScreenshotTool.TakeAsync("rough", "shot_page.html", $"{folder}/rough.png", "1", "16.67", null, null, ct);
		ctx.Assert(rough.ExitCode != 0 && rough.StdErr.Contains("1 decimal", StringComparison.Ordinal), $"Screenshot: a step finer than a tenth of a millisecond is refused — {rough.StdErr}");
		ToolResult outside = await BrowserScreenshotTool.TakeAsync("outside", "shot_page.html", "/tmp/../workspace/out.png", "1", "10", null, null, ct);
		ctx.Assert(outside.ExitCode != 0 && outside.StdErr.Contains("under /tmp/", StringComparison.Ordinal), $"Screenshot: its picture goes under /tmp only — {outside.StdErr}");

		string     refusedFile = $"{folder}/refused.png";
		ToolResult refused     = await ShellTools.BashAsync("refused", $"touch {refusedFile}; chromium --headless --screenshot={refusedFile} shot_page.html", null, ct);
		ctx.Assert(refused.ExitCode != 0 && refused.StdErr.Contains("browser_screenshot", StringComparison.Ordinal) && !File.Exists(refusedFile), $"Screenshot: bash refuses --screenshot and runs nothing, naming the tool — {refused.StdErr}");
		ToolResult refusedReadonly = await ShellTools.ReadonlyBashAsync("refused2", "chromium --screenshot shot_page.html", null, ct);
		ctx.Assert(refusedReadonly.ExitCode != 0 && refusedReadonly.StdErr.Contains("browser_screenshot", StringComparison.Ordinal), $"Screenshot: so does readonly_bash — {refusedReadonly.StdErr}");
		ToolResult lookalike = await ShellTools.BashAsync("lookalike", "echo --screenshots-dir my--screenshot screenshot", null, ct);
		ctx.Assert(lookalike.ExitCode == 0 && lookalike.StdOut.Contains("--screenshots-dir", StringComparison.Ordinal), $"Screenshot: words that only look like the flag are left alone — {lookalike.StdOut} {lookalike.StdErr}");

		// The libraries' own screenshot calls fail, naming the tool, and the rest of each library still works.
		string url = "file://" + Path.GetFullPath("shot_page.html");
		File.WriteAllText("shot_lib.js", $$"""
			const library = process.argv[2];
			(async () => {
				const browser = library === 'playwright' ? await require(library).chromium.launch() : await require(library).launch();
				const page    = await browser.newPage();
				await page.goto('{{url}}');
				console.log('works ' + await page.evaluate(() => 1 + 1));
				const targets = library === 'playwright' ? [page, page.locator('#c'), await page.$('#c')] : [page, await page.$('#c')];
				for (const target of targets) {
					try { await target.screenshot({ path: '{{folder}}/lib.png' }); console.log('captured'); }
					catch (error) { console.log('refused ' + error.message); }
				}
				await browser.close();
			})();
			""");
		File.WriteAllText("shot_lib.py", $$"""
			from playwright.sync_api import sync_playwright
			with sync_playwright() as p:
			    browser = p.chromium.launch()
			    page = browser.new_page()
			    page.goto('{{url}}')
			    print('works', page.evaluate('1 + 1'))
			    for target in [page, page.locator('#c'), page.query_selector('#c')]:
			        try:
			            target.screenshot(path='{{folder}}/lib.png')
			            print('captured')
			        except Exception as error:
			            print('refused', error)
			    browser.close()
			""");
		foreach ((string name, string command, int refusals) in new[] { ("Node Playwright", "node shot_lib.js playwright", 3), ("Puppeteer", "node shot_lib.js puppeteer", 2), ("Python Playwright", "python3 shot_lib.py", 3) })
		{
			ToolResult library = await ShellTools.BashAsync("library", command, null, ct);
			int        refused2 = library.StdOut.Split("refused").Length - 1;
			ctx.Assert(library.StdOut.Contains("works 2", StringComparison.Ordinal) && refused2 == refusals && !library.StdOut.Contains("captured", StringComparison.Ordinal) && !File.Exists($"{folder}/lib.png") && library.StdOut.Contains("browser_screenshot", StringComparison.Ordinal) && library.StdErr.Contains("browser_screenshot", StringComparison.Ordinal),
				$"Screenshot: {name} refuses its screenshot calls, logging why, and still works — {library.StdOut} {library.StdErr}");
		}

		return folder;
	}

	private static async Task RunScriptAsync(TestContext ctx, FakeProvider server, string root, CancellationToken ct)
	{
		bool sandboxed = Sandbox.ShellUserActive;

		// Built first: Build resets the server, which clears any script already queued.
		Rig rig = Build(server);

		server.Enqueue(FakeReply.Call("c1", "bash", Args(("command", "echo hello > shell_made.txt && id -un"))));
		server.Enqueue(FakeReply.Call("c2", "write_file", Args(("file_path", "shell_made.txt"), ("content", "overwritten by write_file\n"))));
		server.Enqueue(FakeReply.Call("c3", "edit_file", Args(("file_path", "shell_made.txt"), ("old_text", "overwritten"), ("new_text", "edited"))));
		server.Enqueue(FakeReply.Call("c4", "bash", Args(("command", "echo appended >> shell_made.txt && cat shell_made.txt"))));
		server.Enqueue(FakeReply.Call("c5", "write_file", Args(("file_path", "sub/dir/new.txt"), ("content", "fresh\n"))));
		server.Enqueue(FakeReply.Call("c6", "edit_file", Args(("file_path", "README.md"), ("old_text", "hello"), ("new_text", "hello again"))));
		server.Enqueue(FakeReply.Call("c7", "bash", Args(("command", "rm -r sub && echo removed && cat README.md"))));
		server.Enqueue(FakeReply.Call("c8", "read_file", Args(("file_path", ".beast/secret.txt"))));
		server.Enqueue(FakeReply.Call("c9", "write_file", Args(("file_path", ".github/ci.yml"), ("content", "evil\n"))));
		server.Enqueue(FakeReply.Call("c10", "bash", Args(("command", "cat .beast/secret.txt; echo evil >> .github/ci.yml; cat /root/.beast/settings.json"))));
		server.Enqueue(FakeReply.Call("c11", "readonly_bash", Args(("command", "cat shell_made.txt"))));
		// Any program the shell runs has the shell user's permissions — a script is no way around them.
		server.Enqueue(FakeReply.Call("c12", "bash", Args(("command",
			"python3 - <<'EOF'\n" +
			"import os\n" +
			"for path, text in [('.github/ci.yml', 'evil'), ('/root/.beast/settings.json', 'evil'), ('.beast/secret.txt', 'evil')]:\n" +
			"    try:\n" +
			"        open(path, 'a').write(text); print('WROTE', path)\n" +
			"    except OSError as e:\n" +
			"        print('refused', path, e.strerror)\n" +
			"try:\n" +
			"    print('READ', open('.beast/secret.txt').read())\n" +
			"except OSError as e:\n" +
			"    print('refused read', e.strerror)\n" +
			"open('python_made.txt', 'w').write('fine')\n" +
			"print('workspace write ok')\n" +
			"EOF"))));
		// A dot-folder the shell creates is the agent's once the command ends; a dot-symlink is removed.
		server.Enqueue(FakeReply.Call("c13", "bash", Args(("command", "mkdir .sneaky && echo x > .sneaky/a && ln -s /tmp .linkdot && echo made"))));
		server.Enqueue(FakeReply.Call("c14", "bash", Args(("command", "echo y >> .sneaky/a; ls -a"))));
		server.Enqueue(FakeReply.Say("all done"));

		rig.Session.Bundle.OnUserMessage("exercise the tools");

		ProtocolResult last = ProtocolResult.Failed("not run");
		for (int i = 0; i < 16 && last.Payload?.AssistantText != "all done"; i++)
			last = await StepAsync(rig, ct);
		ctx.AssertEqual("all done", last.Payload?.AssistantText ?? last.ErrorMessage ?? string.Empty, "Tools: the scripted session runs to the end");

		Dictionary<string, string> results = Results(rig.Session);

		if (sandboxed)
			ctx.Assert(Get(results, "c1").Contains(Sandbox.ShellUser, StringComparison.Ordinal), $"Tools: bash runs as the shell user — {Get(results, "c1")}");
		ctx.Assert(!Get(results, "c2").Contains("Error", StringComparison.Ordinal), $"Tools: write_file overwrites a file the shell made — {Get(results, "c2")}");
		ctx.Assert(!Get(results, "c3").Contains("Error", StringComparison.Ordinal), $"Tools: edit_file edits it — {Get(results, "c3")}");
		ctx.Assert(Get(results, "c4").Contains("edited by write_file", StringComparison.Ordinal) && Get(results, "c4").Contains("appended", StringComparison.Ordinal),
			$"Tools: the shell can still change it after the agent did — {Get(results, "c4")}");
		ctx.Assert(!Get(results, "c5").Contains("Error", StringComparison.Ordinal), $"Tools: write_file creates new folders and files — {Get(results, "c5")}");
		ctx.Assert(!Get(results, "c6").Contains("Error", StringComparison.Ordinal), $"Tools: edit_file edits a file the lockdown handed to the shell user — {Get(results, "c6")}");
		ctx.Assert(Get(results, "c7").Contains("removed", StringComparison.Ordinal) && Get(results, "c7").Contains("hello again", StringComparison.Ordinal),
			$"Tools: the shell can delete what write_file made — {Get(results, "c7")}");
		ctx.Assert(Get(results, "c8").Contains("off limits", StringComparison.Ordinal) && !Get(results, "c8").Contains("TOP-SECRET", StringComparison.Ordinal),
			$"Tools: read_file refuses .beast — {Get(results, "c8")}");
		if (sandboxed)
		{
			ctx.Assert(Get(results, "c9").Contains("protected folder", StringComparison.Ordinal), $"Tools: write_file refuses a locked dot-folder — {Get(results, "c9")}");
			ctx.Assert(!Get(results, "c10").Contains("TOP-SECRET", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(root, ".github", "ci.yml")) == "on: push\n",
				$"Tools: the shell can neither read .beast nor change a locked dot-folder — {Get(results, "c10")}");
			ctx.AssertEqual(Sandbox.ShellUser, (await ShellTools.AgentBashAsync("own", $"stat -c %U '{Path.Combine(root, "shell_made.txt")}'", null, ct)).StdOut.Trim(),
				"Tools: the agent's writes leave the file with the shell user");
		}
		ctx.Assert(Get(results, "c11").Contains("appended", StringComparison.Ordinal), $"Tools: readonly_bash reads it — {Get(results, "c11")}");
		if (sandboxed)
		{
			string script = Get(results, "c12");
			ctx.Assert(!script.Contains("WROTE", StringComparison.Ordinal) && !script.Contains("TOP-SECRET", StringComparison.Ordinal) && script.Contains("workspace write ok", StringComparison.Ordinal),
				$"Tools: a python script in bash writes the workspace but nothing protected — {script}");

			string made13 = Get(results, "c13");
			ctx.Assert(made13.Contains("made", StringComparison.Ordinal) && made13.Contains(".sneaky now belongs to the agent", StringComparison.Ordinal) && made13.Contains("symlink .linkdot was removed", StringComparison.Ordinal),
				$"Tools: a new dot-folder is taken and a dot-symlink removed, and the model is told — {made13}");
			string after14 = Get(results, "c14");
			ctx.Assert(after14.Contains("Permission denied", StringComparison.Ordinal) && !after14.Contains(".linkdot", StringComparison.Ordinal),
				$"Tools: the taken folder is read-only for the shell and the link is gone — {after14}");

			if (Sandbox.FileClockAvailable)
				await FileClockAsync(ctx, root, ct);
			else
				ctx.Log("    SKIP: no clockfs or fuse device, so the counted clock is untested");
		}

		// The date is the constant, not the calendar: a run started on any day counts from the same midnight.
		ctx.AssertEqual(0L, Sandbox.kFileClockBase % 86400, "File clock: the base is a midnight");
		ctx.AssertEqual(DateTimeOffset.Parse("2026-11-23T00:00:00Z").ToUnixTimeSeconds(), Sandbox.kFileClockBase, "File clock: the base is the series' date");

		string madePath = Path.Combine(root, "shell_made.txt");
		string made     = File.Exists(madePath) ? File.ReadAllText(madePath) : "(missing)";
		ctx.AssertEqual("edited by write_file\nappended\n", made, "Tools: the file holds every step's change, in order");
	}

	private sealed class Rig
	{
		public Session Session { get; }
		public LlmService Service { get; }
		public Tool[] Tools { get; }
		public TestCaptureTransport Transport { get; }

		public Rig(Session session, LlmService service, Tool[] tools, TestCaptureTransport transport)
		{
			Session = session;
			Service = service;
			Tools = tools;
			Transport = transport;
		}
	}

	// The production tool set for a role holding the tools under test, built by ToolFactory from the
	// default tool configuration, against a ChatCompletions fake.
	private static Rig Build(FakeProvider server)
	{
		server.Reset();
		ModelConfig config  = new ModelConfig { Id = "fake-tools", Name = "Fake Tools", ContextWindow = 200000, MaxOutputTokens = 4096 };
		LlmModel    model   = new LlmModel(config.Id, server.ChatEndpoint, "fake-key", new List<JsonObject>(), new List<JsonObject>(), config);
		LlmService  service = new LlmService(model, DetectedProtocol.ChatCompletions, new ModelAvailability(), new EndpointFacts(), new List<string> { config.Id }, (id, effort, summaries, retainReasoning, maxImages) => { });

		TestCaptureTransport transport = new TestCaptureTransport();
		BeastSession         data      = new BeastSession(Guid.NewGuid().ToString("N"), "tools", config.Id, "ToolTester",
			string.Empty, new List<CanonicalMessage>(), null, 0m, 0, 0, 0, true);
		Session session = new Session(data, "You are a test model.", transport, false);
		session.UpdateModel(model);

		// The default tool configuration overlays whatever tools.json holds, in memory only, so the
		// rig drives the in-code tool definitions regardless of the local file.
		SettingsService                settings = new SettingsService();
		Dictionary<string, ToolConfig> defaults = ToolService.Defaults();
		foreach (string name in ToolNames)
			settings.Tools[name] = defaults[name];
		Role   role  = new Role("ToolTester", "drives the tools", RoleKind.Subagent, new List<string> { config.Id }, new List<string>(ToolNames), "test", string.Empty, string.Empty);
		Tool[] tools = ToolFactory.BuildForRole(settings, role, null, null, session, false, null, null, null, null, null, null);
		return new Rig(session, service, tools, transport);
	}

	private static async Task<ProtocolResult> StepAsync(Rig rig, CancellationToken ct)
	{
		ProtocolResult result = await rig.Service.RunToCompletionAsync(rig.Session, rig.Tools, null, 0, 0, false, rig.Transport, ct);
		if (result.Outcome == ProtocolCallOutcome.Success)
		{
			rig.Session.CommitAssistantTurn(result.Payload!);
			bool hasCalls = await ToolDispatch.DispatchAsync(result.Payload!, rig.Tools, rig.Session, rig.Transport, ct);
			if (hasCalls)
				rig.Session.CommitToolResults(result.Payload!);
		}
		return result;
	}

	private static string Args(params (string Name, string Value)[] args)
	{
		JsonObject obj = new JsonObject();
		foreach ((string name, string value) in args)
			obj[name] = value;
		return obj.ToJsonString();
	}

	private static Dictionary<string, string> Results(Session session)
	{
		Dictionary<string, string> results = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (CanonicalMessage message in session.Data.Messages)
		{
			if (message is ToolResultMessage tr)
				results[tr.ToolCallId] = tr.Content;
		}
		return results;
	}

	private static string Get(Dictionary<string, string> results, string id)
	{
		return results.TryGetValue(id, out string? content) ? content : "(no result)";
	}
}