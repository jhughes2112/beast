using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;


public static class SearchToolsTests
{
	public static async Task TestAsync(TestContext ctx)
	{
		ctx.Log("  SearchToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_search_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			CreateTestFiles(tempDir);

			TestGlobMatch(ctx);
			await TestGlobAsync(ctx, tempDir);
			await TestListDirectoryAsync(ctx, tempDir);
			await TestGrepAsync(ctx, tempDir);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void CreateTestFiles(string tempDir)
	{
		File.WriteAllText(Path.Combine(tempDir, "hello.cs"), "public class Hello\n{\n    public void Say() { }\n}\n");
		File.WriteAllText(Path.Combine(tempDir, "readme.md"), "# Test Project\nThis is a test.\n");

		string srcDir = Path.Combine(tempDir, "src");
		Directory.CreateDirectory(srcDir);
		File.WriteAllText(Path.Combine(srcDir, "app.cs"), "public class App\n{\n    public void Run() { }\n}\n");
		File.WriteAllText(Path.Combine(srcDir, "app.ts"), "export class App {\n  run() { }\n}\n");

		string utilsDir = Path.Combine(srcDir, "utils");
		Directory.CreateDirectory(utilsDir);
		File.WriteAllText(Path.Combine(utilsDir, "helper.cs"), "public class Helper\n{\n    public int Add(int a, int b) { return a + b; }\n}\n");
	}

	private static void TestGlobMatch(TestContext ctx)
	{
		// ** matches any depth.
		ctx.Assert(SearchTools.GlobMatch("hello.cs", "**/*.cs"), "GlobMatch: **/*.cs matches root file");
		ctx.Assert(SearchTools.GlobMatch("src/app.cs", "**/*.cs"), "GlobMatch: **/*.cs matches nested file");
		ctx.Assert(SearchTools.GlobMatch("a/b/c/deep.cs", "**/*.cs"), "GlobMatch: **/*.cs matches deeply nested file");
		ctx.Assert(!SearchTools.GlobMatch("hello.ts", "**/*.cs"), "GlobMatch: **/*.cs rejects wrong extension");

		// Single * does not cross directories.
		ctx.Assert(SearchTools.GlobMatch("hello.cs", "*.cs"), "GlobMatch: *.cs matches root file");
		ctx.Assert(!SearchTools.GlobMatch("src/hello.cs", "*.cs"), "GlobMatch: *.cs rejects nested file");

		// Subdirectory prefix.
		ctx.Assert(SearchTools.GlobMatch("src/app.cs", "src/**/*.cs"), "GlobMatch: src/**/*.cs matches direct child");
		ctx.Assert(SearchTools.GlobMatch("src/utils/helper.cs", "src/**/*.cs"), "GlobMatch: src/**/*.cs matches nested child");
		ctx.Assert(!SearchTools.GlobMatch("hello.cs", "src/**/*.cs"), "GlobMatch: src/**/*.cs rejects root file");

		// ? matches single character.
		ctx.Assert(SearchTools.GlobMatch("a.cs", "?.cs"), "GlobMatch: ?.cs matches single char");
		ctx.Assert(!SearchTools.GlobMatch("ab.cs", "?.cs"), "GlobMatch: ?.cs rejects two chars");
	}

	private static async Task TestGlobAsync(TestContext ctx, string tempDir)
	{
		string srcDir = Path.Combine(tempDir, "src");

		// Find all .cs files recursively.
		ToolResult allCs = await SearchTools.GlobAsync("**/*.cs", tempDir);
		ctx.Assert(allCs.Response.Contains("hello.cs"), "Glob: finds root .cs file");
		ctx.Assert(allCs.Response.Contains("src/app.cs"), "Glob: finds nested .cs file");
		ctx.Assert(allCs.Response.Contains("src/utils/helper.cs"), "Glob: finds deeply nested .cs file");
		ctx.Assert(!allCs.Response.Contains("app.ts"), "Glob: excludes non-.cs files");

		// Find .ts files.
		ToolResult tsFiles = await SearchTools.GlobAsync("**/*.ts", tempDir);
		ctx.Assert(tsFiles.Response.Contains("app.ts"), "Glob: finds .ts file");

		// Non-recursive pattern.
		ToolResult shallowCs = await SearchTools.GlobAsync("*.cs", tempDir);
		ctx.Assert(shallowCs.Response.Contains("hello.cs"), "Glob: shallow pattern finds root file");
		ctx.Assert(!shallowCs.Response.Contains("src/app.cs"), "Glob: shallow pattern skips nested files");

		// Scoped to subdirectory.
		ToolResult srcOnly = await SearchTools.GlobAsync("**/*.cs", srcDir);
		ctx.Assert(srcOnly.Response.Contains("app.cs"), "Glob: scoped to subdirectory finds files");
		ctx.Assert(!srcOnly.Response.Contains("hello.cs"), "Glob: scoped to subdirectory excludes parent files");

		// No matches.
		ToolResult noMatch = await SearchTools.GlobAsync("**/*.xyz", tempDir);
		ctx.Assert(noMatch.Response.Contains("No files found"), "Glob: no matches returns message");

		// Empty pattern.
		ToolResult emptyPattern = await SearchTools.GlobAsync("", tempDir);
		ctx.Assert(emptyPattern.Response.Contains("Error"), "Glob: empty pattern returns error");

		// Empty path.
		ToolResult emptyPath = await SearchTools.GlobAsync("**/*.cs", "");
		ctx.Assert(emptyPath.Response.Contains("Error"), "Glob: empty path returns error");
	}

	private static async Task TestListDirectoryAsync(TestContext ctx, string tempDir)
	{
		string srcDir = Path.Combine(tempDir, "src");

		// List root.
		ToolResult root = await SearchTools.ListDirectoryAsync(tempDir, null);
		ctx.Assert(root.Response.Contains("src/"), "ListDirectory: shows subdirectory with trailing slash");
		ctx.Assert(root.Response.Contains("hello.cs"), "ListDirectory: shows files");
		ctx.Assert(root.Response.Contains("readme.md"), "ListDirectory: shows all files");

		// List subdirectory.
		ToolResult src = await SearchTools.ListDirectoryAsync(srcDir, null);
		ctx.Assert(src.Response.Contains("app.cs"), "ListDirectory: lists subdirectory contents");
		ctx.Assert(src.Response.Contains("utils/"), "ListDirectory: shows nested subdirectory");

		// Non-existent directory.
		string missingDir = Path.Combine(tempDir, "nonexistent");
		ToolResult missing = await SearchTools.ListDirectoryAsync(missingDir, null);
		ctx.Assert(missing.Response.Contains("Error"), "ListDirectory: non-existent returns error");

		// Empty path.
		ToolResult emptyPath = await SearchTools.ListDirectoryAsync("", null);
		ctx.Assert(emptyPath.Response.Contains("Error"), "ListDirectory: empty path returns error");
	}

	private static async Task TestGrepAsync(TestContext ctx, string tempDir)
	{
		// Basic grep.
		ToolResult filesMode = await SearchTools.GrepAsync(tempDir, "class", null);
		ctx.Assert(filesMode.Response.Contains("hello.cs"), "Grep: finds root match");
		ctx.Assert(filesMode.Response.Contains("src/app.cs"), "Grep: finds nested match");

		// Content mode shows matching lines.
		ToolResult contentMode = await SearchTools.GrepAsync(tempDir, "class Hello", null);
		ctx.Assert(contentMode.Response.Contains("public class Hello"), "Grep: shows matching line");
		ctx.Assert(contentMode.Response.Contains("hello.cs"), "Grep: shows filename");

		// No matches.
		ToolResult noMatch = await SearchTools.GrepAsync(tempDir, "zzzzzznonexistent", null);
		ctx.Assert(noMatch.Response.Contains("0 match"), "Grep: no matches returns zero count");

		// Empty pattern.
		ToolResult emptyPattern = await SearchTools.GrepAsync(tempDir, "", null);
		ctx.Assert(emptyPattern.Response.Contains("Error"), "Grep: empty pattern returns error");

		// Empty path.
		ToolResult emptyPath = await SearchTools.GrepAsync("", "class", null);
		ctx.Assert(emptyPath.Response.Contains("Error"), "Grep: empty path returns error");
	}
}