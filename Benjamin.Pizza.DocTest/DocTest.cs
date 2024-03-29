using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.Scripting;

using Xunit;

namespace Benjamin.Pizza.DocTest;

/// <summary>A doctest.</summary>
/// <example name="A test for the doctest">
/// <code doctest="true">
/// Console.WriteLine("Hello world");
/// // Output:
/// // Hello world
/// </code>
/// </example>
[SuppressMessage("naming", "CA1724:The type name conflicts in whole or in part with the namespace name", Justification = "DGAF")]
public class DocTest
{
    private static readonly Regex _outputRegex = new(@"// Output:\s*", RegexOptions.Compiled);
    private static readonly Regex _commentRegex = new(@"^\s*(//( |$))?", RegexOptions.Compiled);
    private readonly Script _script;
    private readonly string _name;
    private readonly string _code;

    /// <summary>Constructor.</summary>
    /// <param name="name">The name of the test.</param>
    /// <param name="code">The test's code.</param>
    /// <param name="preamble">Code to prepend to the <paramref name="code"/>.</param>
    public DocTest(string name, string code, Script preamble)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(preamble);

        _script = preamble.ContinueWith(code);
        _code = code;
        _name = name;
    }

    /// <summary>ToString.</summary>
    /// <returns>A string.</returns>
    public override string ToString() => _name;

    /// <summary>Run the test.</summary>
    /// <returns>A task.</returns>
    public async Task Run()
    {
        var (output, error) = await RedirectConsole(() => _script.RunAsync()).ConfigureAwait(false);
        Assert.Equal("", error);
        Assert.Equal(GetExpected(), SplitLines(output));
    }

    private IEnumerable<string> GetExpected()
    {
        var match = _outputRegex.Match(_code);
        return SplitLines(_code[(match.Index + match.Length)..])
            .Select(line => _commentRegex.Replace(line, ""));
    }

    private static async Task<(string Output, string Error)> RedirectConsole(Func<Task> action)
    {
        using var outBuffer = new StringWriter();
        using var errBuffer = new StringWriter();
        var oldConsoleOut = Console.Out;
        var oldConsoleErr = Console.Error;
        Console.SetOut(outBuffer);
        Console.SetError(errBuffer);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(oldConsoleOut);
            Console.SetError(oldConsoleErr);
        }

        return (outBuffer.ToString(), errBuffer.ToString());
    }

    private static readonly string[] _newlines = { "\r\n", "\n" };

    private static string[] SplitLines(string str)
        => str.Split(_newlines, StringSplitOptions.None);
}
