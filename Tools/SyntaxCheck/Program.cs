// Syntax check for the Unity scripts (no Unity needed): dotnet run -- ../../Assets
using Microsoft.CodeAnalysis; using Microsoft.CodeAnalysis.CSharp;
int bad = 0;
foreach (var f in Directory.GetFiles(args[0], "*.cs", SearchOption.AllDirectories))
    foreach (var d in CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.CSharp9), f).GetDiagnostics())
        if (d.Severity == DiagnosticSeverity.Error) { Console.WriteLine(d); bad++; }
Console.WriteLine(bad == 0 ? "syntax OK" : bad + " syntax errors");
return bad;
