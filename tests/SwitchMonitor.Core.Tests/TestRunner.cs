using System.Reflection;

namespace SwitchMonitor.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute : Attribute { }

internal static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertFailed(message);
    }

    public static void Equal<T>(T expected, T actual, string? label = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailed($"{label ?? "value"}: expected <{expected}>, got <{actual}>");
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? label = null)
    {
        var e = expected.ToArray();
        var a = actual.ToArray();
        if (!e.SequenceEqual(a))
            throw new AssertFailed($"{label ?? "sequence"}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException error) { return error; }
        catch (Exception other) { throw new AssertFailed($"expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}"); }
        throw new AssertFailed($"expected {typeof(TException).Name}, nothing was thrown");
    }
}

internal sealed class AssertFailed(string message) : Exception(message);

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string? filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.GetCustomAttribute<TestAttribute>() is not null)
            .Select(method => (Name: $"{method.DeclaringType!.Name}.{method.Name}", Method: method))
            .Where(test => filter is null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(test => test.Name, StringComparer.Ordinal)
            .ToArray();

        int failed = 0;
        foreach (var (name, method) in tests)
        {
            try
            {
                method.Invoke(null, null);
                Console.WriteLine($"PASS  {name}");
            }
            catch (TargetInvocationException wrapper) when (wrapper.InnerException is not null)
            {
                failed++;
                Console.WriteLine($"FAIL  {name}: {wrapper.InnerException.GetType().Name}: {wrapper.InnerException.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed (no monitor command sent).");
        if (tests.Length == 0) { Console.WriteLine("No tests found."); return 2; }
        return failed == 0 ? 0 : 1;
    }
}
