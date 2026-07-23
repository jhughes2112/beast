using System;
using System.Reflection;


// Lightweight in-process test harness.
public class TestContext
{
	private readonly ITransportServer _transport;
	private int _passed;
	private int _failed;

	public int Passed => _passed;
	public int Failed => _failed;

	public TestContext(ITransportServer transport)
	{
		_transport = transport;
	}

	// Log an informational line (section headers, skip notices, etc.).
	public void Log(string text)
	{
		_transport.Output("", text);
	}

	public void Assert(bool condition, string testName)
	{
		if (condition)
		{
			_passed++;
		}
		else
		{
			_failed++;
			_transport.Output("", $"  FAIL: {testName}");
		}
	}

	public void AssertEqual<T>(T expected, T actual, string testName)
	{
		bool equal = (expected == null && actual == null) || (expected != null && expected.Equals(actual));
		Assert(equal, $"{testName} — expected '{expected}' got '{actual}'");
	}

	public void AssertNull(object? value, string testName)
	{
		Assert(value == null, $"{testName} — expected null got '{value}'");
	}

	public void AssertNotNull(object? value, string testName)
	{
		Assert(value != null, $"{testName} — expected non-null");
	}

	public void AssertContains(string haystack, string needle, string testName)
	{
		Assert(haystack.Contains(needle), $"{testName} — expected '{needle}' in: {haystack}");
	}
}

// Reflection helper to invoke private methods without modifying the target classes. The generic
// parameters carry DynamicallyAccessedMembers(All) so Native AOT keeps full reflection metadata
// for every concrete type the tests reflect over — call sites name the type statically through
// inference, which is exactly what the AOT compiler needs to root the members.
public static class Reflect
{
	public static object? Instance<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(T target, string methodName, Type[] parameterTypes, object[] arguments) where T : notnull
	{
		MethodInfo method = typeof(T).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Instance method '{methodName}' not found on {typeof(T).Name}");
		return method.Invoke(target, arguments);
	}

	public static object? Static([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] Type type, string methodName, Type[] parameterTypes, object[] arguments)
	{
		MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Static method '{methodName}' not found on {type.Name}");
		return method.Invoke(null, arguments);
	}

	public static void SetField<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(T target, string fieldName, object? value) where T : notnull
	{
		FieldInfo field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException($"Field '{fieldName}' not found on {typeof(T).Name}");
		field.SetValue(target, value);
	}

	public static object? GetField<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(T target, string fieldName) where T : notnull
	{
		FieldInfo field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException($"Field '{fieldName}' not found on {typeof(T).Name}");
		return field.GetValue(target);
	}
}