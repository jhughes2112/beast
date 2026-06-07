using System;
using System.IO;
using System.Threading.Tasks;
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
			await TestReadAsync(ctx, tempDir);
			await TestEditExactAsync(ctx, tempDir);
			await TestEditFuzzyAsync(ctx, tempDir);
			await TestEditNotFoundAsync(ctx, tempDir);
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

	private static async Task TestReadAsync(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "read.txt");
		File.WriteAllText(path, "line one\nline two\nline three\n");

		using CancellationTokenSource cts = new CancellationTokenSource();

		// Full read returns raw content
		ToolResult full = await FileTools.ReadFileAsync(path, string.Empty, string.Empty, cts.Token);
		ctx.Assert(full.ExitCode == 0, "Read full: success");
		ctx.AssertContains(full.StdOut, "line one", "Read full: has line one");
		ctx.AssertContains(full.StdOut, "line three", "Read full: has line three");
		ctx.Assert(!full.StdOut.Contains(":"), "Read full: no hash anchors");

		// Windowed read
		ToolResult windowed = await FileTools.ReadFileAsync(path, "2", "1", cts.Token);
		ctx.Assert(windowed.ExitCode == 0, "Read windowed: success");
		ctx.AssertContains(windowed.StdOut, "line two", "Read windowed: has line two");
		ctx.Assert(!windowed.StdOut.Contains("line one"), "Read windowed: no line one");
	}

	private static async Task TestEditExactAsync(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "edit_exact.txt");
		File.WriteAllText(path, "first line\nsecond line\nthird line\n");

		using CancellationTokenSource cts = new CancellationTokenSource();

		// Single-line exact replacement
		ToolResult rep = await FileTools.EditFileAsync(path, "second line", "SECOND LINE", cts.Token);
		ctx.AssertContains(rep.StdOut, "OK", $"Edit exact single: OK (err: {rep.StdErr})");
		string after = File.ReadAllText(path);
		ctx.AssertContains(after, "SECOND LINE", "Edit exact single: updated");
		ctx.Assert(!after.Contains("second line"), "Edit exact single: old text gone");

		// Multi-line exact replacement
		ToolResult rep2 = await FileTools.EditFileAsync(path, "SECOND LINE\nthird line", "replaced A\nreplaced B", cts.Token);
		ctx.AssertContains(rep2.StdOut, "OK", $"Edit exact multi: OK (err: {rep2.StdErr})");
		string after2 = File.ReadAllText(path);
		ctx.AssertContains(after2, "replaced A", "Edit exact multi: has replaced A");
		ctx.AssertContains(after2, "replaced B", "Edit exact multi: has replaced B");
	}

	private static async Task TestEditFuzzyAsync(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "edit_fuzzy.txt");
		File.WriteAllText(path, "    public void Foo()\n    {\n        return;\n    }\n");

		using CancellationTokenSource cts = new CancellationTokenSource();

		// old_text has different indentation — fuzzy match should still work
		string oldText = "public void Foo()\n{\nreturn;\n}";
		ToolResult rep = await FileTools.EditFileAsync(path, oldText, "public void Bar() { }", cts.Token);
		ctx.AssertContains(rep.StdOut, "OK", $"Edit fuzzy: OK (err: {rep.StdErr})");
		string after = File.ReadAllText(path);
		ctx.AssertContains(after, "Bar", "Edit fuzzy: replacement applied");
	}

	private static async Task TestEditNotFoundAsync(TestContext ctx, string tempDir)
	{
		string path = Path.Combine(tempDir, "edit_notfound.txt");
		File.WriteAllText(path, "some content here");

		using CancellationTokenSource cts = new CancellationTokenSource();

		ToolResult bad = await FileTools.EditFileAsync(path, "text that does not exist", "replacement", cts.Token);
		ctx.Assert(bad.ExitCode != 0, "Edit not found: error exit code");
		ctx.AssertContains(bad.StdErr, "Error", "Edit not found: error message");
		string content = File.ReadAllText(path);
		ctx.AssertContains(content, "some content here", "Edit not found: file unchanged");
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
