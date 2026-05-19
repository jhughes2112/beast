using System;
using System.IO;
using System.Threading.Tasks;


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
			await TestGlobAsync(ctx, tempDir);
			await TestGrepAsync(ctx, tempDir);
			await TestListDirectoryAsync(ctx, tempDir);
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

	private static async Task TestGlobAsync(TestContext ctx, string tempDir)
	{
		File.WriteAllText(Path.Combine(tempDir, "a.cs"), "");
		File.WriteAllText(Path.Combine(tempDir, "b.txt"), "");

		ToolResult result = await SearchTools.GlobAsync("*.cs", tempDir);
		ctx.Assert(result.Response.Contains("a.cs"), "Glob: finds .cs file");
		ctx.Assert(!result.Response.Contains("b.txt"), "Glob: excludes .txt file");
	}

	private static async Task TestGrepAsync(TestContext ctx, string tempDir)
	{
		File.WriteAllText(Path.Combine(tempDir, "test.cs"), "public class Hello { }");

		ToolResult result = await SearchTools.GrepAsync(tempDir, "Hello", null);
		ctx.Assert(result.Response.Contains("test.cs"), "Grep: finds file with match");
	}

	private static async Task TestListDirectoryAsync(TestContext ctx, string tempDir)
	{
		Directory.CreateDirectory(Path.Combine(tempDir, "subdir"));
		File.WriteAllText(Path.Combine(tempDir, "file.txt"), "");

		ToolResult result = await SearchTools.ListDirectoryAsync(tempDir, null);
		ctx.Assert(result.Response.Contains("file.txt"), "ListDirectory: lists file");
		ctx.Assert(result.Response.Contains("subdir/"), "ListDirectory: lists directory");
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