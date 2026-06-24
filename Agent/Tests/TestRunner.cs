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

// Reflection helper to invoke private methods without modifying the target classes.
public static class Reflect
{
	public static object? Instance(object target, string methodName, Type[] parameterTypes, object[] arguments)
	{
		MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Instance method '{methodName}' not found on {target.GetType().Name}");
		return method.Invoke(target, arguments);
	}

	public static object? Static(Type type, string methodName, Type[] parameterTypes, object[] arguments)
	{
		MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Static method '{methodName}' not found on {type.Name}");
		return method.Invoke(null, arguments);
	}

	public static void SetField(object target, string fieldName, object? value)
	{
		FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
		field.SetValue(target, value);
	}

	public static object? GetField(object target, string fieldName)
	{
		FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
		return field.GetValue(target);
	}
}