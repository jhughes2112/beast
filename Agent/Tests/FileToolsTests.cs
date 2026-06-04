using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Threading;


public static class FileToolsTests
{
	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  FileToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			TestWriteAndRead(ctx, tempDir);
			TestAppend(ctx, tempDir);
			TestEdit(ctx, tempDir);
			await TestEditOperationsAsync(ctx, tempDir);
			TestReadBinary(ctx, tempDir);
			TestWriteBinary(ctx, tempDir);
		}
		finally
		{
			Directory.Delete(tempDir, true);
		}
	}

	private static void TestWriteAndRead(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "test.txt");
		File.WriteAllText(path, "hello world");
		string content = File.ReadAllText(path);
		ctx.AssertEqual("hello world", content, "WriteAndRead: content matches");
	}

	private static void TestAppend(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "append.txt");
		File.WriteAllText(path, "first");
		File.AppendAllText(path, " second");
		string content = File.ReadAllText(path);
		ctx.AssertEqual("first second", content, "Append: content matches");
	}

	private static void TestEdit(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "edit.txt");
		File.WriteAllText(path, "hello world");
		// Simple edit test
		string content = File.ReadAllText(path);
		ctx.Assert(content.Contains("hello"), "Edit: original content present");
	}

	private static string ExtractAnchor(string numberedLine)
	{
		// numberedLine is in the format "<line>:<hh>\t<content>"
		int colon = numberedLine.IndexOf(':');
		if (colon < 0) return string.Empty;
		int tab = numberedLine.IndexOf('\t');
		string lineNum = tab > colon ? numberedLine.Substring(0, colon) : string.Empty;
		if (string.IsNullOrEmpty(lineNum)) return string.Empty;
		string hashPart = numberedLine.Substring(colon + 1, 2);
		return lineNum + ":" + hashPart;
	}

	private static async Task TestEditOperationsAsync(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "edits.txt");
		string initial = "first line\nsecond line\nthird line\nfourth line\n";
		File.WriteAllText(path, initial);

		// Read file to get anchors
		using CancellationTokenSource cts0 = new CancellationTokenSource();
		ToolResult read = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts0.Token);
		ctx.Assert(!read.StdOut.Contains("Error"), "Read anchors: no error");
		string[] lines = read.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

		// Find anchors for lines 1..4
		string anchor1 = ExtractAnchor(lines[0]);
		string anchor2 = ExtractAnchor(lines[1]);
		string anchor3 = ExtractAnchor(lines[2]);
		string anchor4 = ExtractAnchor(lines[3]);

		// Single-line replace: replace second line
		using CancellationTokenSource cts1 = new CancellationTokenSource();
		ToolResult rep1 = await FileTools.EditFileReplaceAsync(path, anchor2, anchor2, "SECOND LINE", cts1.Token);
		ctx.Assert(rep1.StdOut.Contains("OK"), "Edit replace single: OK");
		string after1 = File.ReadAllText(path);
		ctx.Assert(after1.Contains("SECOND LINE"), "Edit replace single: content updated");

		// Multi-line replace: replace lines 2-3
		using CancellationTokenSource cts2 = new CancellationTokenSource();
		read = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts2.Token);
		lines = read.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		anchor2 = ExtractAnchor(lines[1]);
		anchor3 = ExtractAnchor(lines[2]);
		using CancellationTokenSource cts3 = new CancellationTokenSource();
		ToolResult rep2 = await FileTools.EditFileReplaceAsync(path, anchor2, anchor3, "replaced A\nreplaced B", cts3.Token);
		ctx.Assert(rep2.StdOut.Contains("OK"), "Edit replace multi: OK");
		string after2 = File.ReadAllText(path);
		ctx.Assert(after2.Contains("replaced A") && after2.Contains("replaced B"), "Edit replace multi: content updated");

		// Insert after: insert after line 1
		using CancellationTokenSource cts4 = new CancellationTokenSource();
		read = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts4.Token);
		lines = read.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		anchor1 = ExtractAnchor(lines[0]);
		using CancellationTokenSource cts5 = new CancellationTokenSource();
		ToolResult ins1 = await FileTools.EditFileInsertAsync(path, anchor1, "inserted line", cts5.Token);
		ctx.Assert(ins1.StdOut.Contains("OK"), "Edit insert after: OK");
		string after3 = File.ReadAllText(path);
		ctx.Assert(after3.Contains("inserted line"), "Edit insert after: content updated");

		// Combined operations: call replace and insert separately
		using CancellationTokenSource cts6 = new CancellationTokenSource();
		read = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts6.Token);
		lines = read.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		anchor1 = ExtractAnchor(lines[0]);
		anchor4 = ExtractAnchor(lines[lines.Length - 1]);

		using CancellationTokenSource cts7 = new CancellationTokenSource();
		ToolResult rep3 = await FileTools.EditFileReplaceAsync(path, anchor1, anchor1, "FIRST-UPDATED", cts7.Token);
		ctx.Assert(rep3.StdOut.Contains("OK"), "Edit combined ops replace: OK");

		using CancellationTokenSource cts7b = new CancellationTokenSource();
		read = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts7b.Token);
		lines = read.StdOut.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		anchor4 = ExtractAnchor(lines[lines.Length - 1]);

		using CancellationTokenSource cts7c = new CancellationTokenSource();
		ToolResult ins2 = await FileTools.EditFileInsertAsync(path, anchor4, "ADDED-AT-END", cts7c.Token);
		ctx.Assert(ins2.StdOut.Contains("OK"), "Edit combined ops insert: OK");

		string after4 = File.ReadAllText(path);
		ctx.Assert(after4.Contains("FIRST-UPDATED") && after4.Contains("ADDED-AT-END"), "Edit combined ops: content updated");

		// Mismatch scenario: attempt to replace using stale anchor should error and not apply
			// Use the original anchor2 which is now stale
			using CancellationTokenSource cts8 = new CancellationTokenSource();
			ToolResult bad = await FileTools.EditFileReplaceAsync(path, anchor2, anchor2, "SHOULD-NOT-APPLY", cts8.Token);
			ctx.Assert(bad.StdErr.Contains("Error") || bad.ExitCode != 0, "Edit mismatch: returns error");
			string final = File.ReadAllText(path);
			ctx.Assert(!final.Contains("SHOULD-NOT-APPLY"), "Edit mismatch: content unchanged");
		}

	private static void TestReadBinary(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "binary.bin");
		byte[] data = new byte[] { 0x01, 0x02, 0x03 };
		File.WriteAllBytes(path, data);
		byte[] read = File.ReadAllBytes(path);
		ctx.Assert(read.Length == 3, "ReadBinary: length matches");
	}

	private static void TestWriteBinary(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "write.bin");
		byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
		File.WriteAllBytes(path, data);
		byte[] read = File.ReadAllBytes(path);
		ctx.Assert(read.Length == 4, "WriteBinary: length matches");
	}
}
