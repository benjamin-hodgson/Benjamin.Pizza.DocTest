using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Benjamin.Pizza.DocTest.SourceGenerator;

/// <summary>
/// Source generator for doctests.
/// </summary>
[Generator]
public class DocTestSourceGenerator : IIncrementalGenerator
{
    private static readonly Regex _outputRegex = new(@"// Output:\s*", RegexOptions.Compiled);
    private static readonly Regex _commentRegex = new(@"^\s*(//( |$))?", RegexOptions.Compiled);
    private static readonly Regex _specialCharsRegex = new(@"[ <>*~`'"".,_\-+&#^@]", RegexOptions.Compiled);

#pragma warning disable RS2008  // Enable analyzer release tracking
    private static readonly DiagnosticDescriptor _missingDocFile
        = new(
            "DOCTEST0001",
            "Missing XML documentation file",
            "Add an AdditionalFiles item for {0}",
            "DocTest",
            DiagnosticSeverity.Error,
            true
        );

    private static readonly DiagnosticDescriptor _mustBePublic
        = new(
            "DOCTEST0002",
            "Class must be public",
            "Mark {0} as public",
            "DocTest",
            DiagnosticSeverity.Error,
            true
        );

    private static readonly DiagnosticDescriptor _mustBePartial
        = new(
            "DOCTEST0003",
            "Class must be partial",
            "Mark {0} as partial",
            "DocTest",
            DiagnosticSeverity.Error,
            true
        );
#pragma warning restore RS2008  // Enable analyzer release tracking

    internal const string ConsoleRedirectorSourceCode =
        """
        // ------------------------------------------------------------------
        // <auto-generated>
        //     This code was generated by DocTest.
        // </auto-generated>
        // ------------------------------------------------------------------
        namespace Benjamin.Pizza.DocTest;

        internal sealed class ConsoleRedirector : System.IDisposable
        {
            private readonly System.IO.StringWriter _outBuffer;
            private readonly System.IO.StringWriter _errBuffer;
            private readonly System.IO.TextWriter _oldConsoleOut;
            private readonly System.IO.TextWriter _oldConsoleErr;

            public string CapturedConsoleOut => _outBuffer.ToString();

            public string CapturedConsoleError => _errBuffer.ToString();

            internal ConsoleRedirector()
            {
                _outBuffer = new System.IO.StringWriter();
                _errBuffer = new System.IO.StringWriter();
                _oldConsoleOut = System.Console.Out;
                _oldConsoleErr = System.Console.Error;

                System.Console.SetOut(_outBuffer);
                System.Console.SetError(_errBuffer);
            }

            public void Dispose()
            {
                System.Console.SetOut(_oldConsoleOut);
                System.Console.SetError(_oldConsoleErr);

                // Safe to dispose as StringWriter does not
                // destroy its internal stringbuilder upon disposal
                _outBuffer.Dispose();
                _errBuffer.Dispose();
            }
        }
        """;

    internal const string DocTestAttributeSourceCode =
        """
        // ------------------------------------------------------------------
        // <auto-generated>
        //     This code was generated by DocTest.
        // </auto-generated>
        // ------------------------------------------------------------------
        namespace Benjamin.Pizza.DocTest;

        /// <summary>
        /// Generate doctests for the given assembly.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
        internal sealed class DocTestAttribute : Attribute
        {
            /// <summary>
            /// A type in the assembly for which to generate doctests.
            /// </summary>
            public Type TypeInAssemblyToDoctest { get; }

            /// <summary>
            /// An array of namespaces to bring into scope in the doctests.
            /// </summary>
            public string[] Usings { get; set; }

            /// <summary>
            /// Creates the attribute.
            /// </summary>
            /// <param name="typeInAssemblyToDoctest">A type in the assembly for which to generate doctests.</param>
            public DocTestAttribute(Type typeInAssemblyToDoctest)
            {
                TypeInAssemblyToDoctest = typeInAssemblyToDoctest;
            }
        }
        """;

    /// <summary>
    /// Initialise the source generator.
    /// </summary>
    /// <param name="context">The context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(
            ctx =>
            {
                ctx.AddSource("ConsoleRedirector", ConsoleRedirectorSourceCode);
                ctx.AddSource("DocTestAttribute", DocTestAttributeSourceCode);
            }
        );
        var assemblies = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Benjamin.Pizza.DocTest.DocTestAttribute",
            (_, _) => true,
            (ctx, ct) =>
            {
                var clsSyntax = (ClassDeclarationSyntax)ctx.TargetNode;
                var cls = ctx.TargetSymbol;
                var attr = ctx.Attributes.Single();
                var typeInAssembly = (INamedTypeSymbol)attr.ConstructorArguments.Single().Value!;
                var usingsTc = attr.NamedArguments.SingleOrDefault(a => a.Key == "Usings").Value;
                var usings = usingsTc.Kind == TypedConstantKind.Array && !usingsTc.IsNull
                    ? usingsTc.Values.Select(u => (string?)u.Value ?? "").ToImmutableArray()
                    : [];
                return (attr, usings, clsSyntax, cls, asm: typeInAssembly.ContainingAssembly);
            });

        var documentationFiles = assemblies
            .Combine(context.AdditionalTextsProvider.Where(x => Path.GetExtension(x.Path) == ".xml").Collect())
            .Select((tup, ct) => (
                attr: tup.Left.attr,
                usings: tup.Left.usings,
                clsSyntax: tup.Left.clsSyntax,
                cls: tup.Left.cls,
                docPath: tup.Left.asm.Identity.Name + ".xml",
                doc: tup.Right
                    .Where(x => Path.GetFileName(x.Path) == tup.Left.asm.Identity.Name + ".xml")
                    .SingleOrDefault()
            ));

        context.RegisterSourceOutput(
            documentationFiles,
            (ctx, tup) =>
            {
                var attrSyntaxRef = tup.attr.ApplicationSyntaxReference;
                var attrLocation = Location.Create(attrSyntaxRef!.SyntaxTree, attrSyntaxRef.Span);

                if (!tup.clsSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(_mustBePartial, attrLocation, tup.cls.Name));
                    return;
                }

                if (!tup.clsSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(_mustBePublic, attrLocation, tup.cls.Name));
                    return;
                }

                if (tup.doc == null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(_missingDocFile, attrLocation, tup.docPath));
                    return;
                }

                var ns = tup.cls.ContainingNamespace != null
                    ? $"namespace {tup.cls.ContainingNamespace.ToDisplayString()};"
                    : "";

                var source = $$"""
                    // ------------------------------------------------------------------
                    // <auto-generated>
                    //     This code was generated by DocTest.
                    // </auto-generated>
                    // ------------------------------------------------------------------
                    {{GetUsings(tup.usings)}}

                    {{ns}}

                    public partial class {{tup.cls.Name}}
                    {
                    {{string.Join("\n\n", CreateMethods(tup.doc))}}
                    }
                    """;

                ctx.AddSource(tup.cls.Name + ".DocTest.g.cs", source);
            }
        );
    }

    private string GetUsings(IEnumerable<string> usings)
        => string.Join("\n", usings.Select(u => $"using {u};"));

    private IEnumerable<string> CreateMethods(AdditionalText doc)
    {
        var xml = XDocument.Parse(doc.GetText()!.ToString());
        return
            from mem in xml.Descendants()
            where mem.Name == "member"
            from ex in mem.Descendants()
            where ex.Name == "example"
            let codes = ex
                .Elements()
                .Where(c => c.Name == "code" && c.Attribute("doctest")?.Value == "true")
                .Select((x, i) => (ix: i, code: x.Value))
            from c in codes
            let name = ex.Attribute("name")!.Value
                + (codes.Count() > 1 ? " > " + c.ix : "")
            select GetMethod(name, c.code);
    }

    private string GetMethod(string name, string code)
    {
        var methodName = GetMethodName(name);
        return $$"""
                [Xunit.Fact(DisplayName = {{SyntaxFactory.Literal(name)}})]
                [System.CodeDom.Compiler.GeneratedCode("Benjamin.Pizza.DocTest", "1.0.0")]
                #pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
                public static async Task {{methodName}}()
                #pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
                {
                    var __doctest_redirector = new Benjamin.Pizza.DocTest.ConsoleRedirector();
                    using (__doctest_redirector)
                    {
                        {{code.Trim()}}
                    }

                    Xunit.Assert.Equal("", __doctest_redirector.CapturedConsoleError);
                    Xunit.Assert.Equal(
                        [
                            {{GetExpected(code)}}
                        ],
                        __doctest_redirector.CapturedConsoleOut.Split(
                            ["\r\n", "\n"],
                            System.StringSplitOptions.None
                        )
                    );
                }
            """;
    }

    private string GetMethodName(string name)
        => _specialCharsRegex.Replace(name, "_");

    private string GetExpected(string code)
    {
        var match = _outputRegex.Match(code);
        return string.Join(
            ",\n                ",
            code.Substring(match.Index + match.Length)
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line => SyntaxFactory.Literal(_commentRegex.Replace(line, "")))
        );
    }
}
