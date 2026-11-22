using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CSharpScopeRangesTests
{
    [Test]
    public void IncludesConstructorsWithBlockAndExpressionBodies()
    {
        var ranges = Read("class Example\n{\npublic Example(int arg) : this()\n{\n}\n"
            + "private Example() =>\n Init();\n}")!;
        Assert.That(ranges.Format(ranges.Find(4)), Is.EqualTo("Example › Example"));
        Assert.That(ranges.Format(ranges.Find(7)), Is.EqualTo("Example › Example"));
    }

    [Test]
    public void FollowsNestedRangesAndMultilineHeadersInBothDirections()
    {
        var ranges = Read("""
            namespace Sample;
            class Outer
            {
                class Inner
                {
                    int First(
                        int value)
                    {
                        if (true) { return value; }
                    }
                    string Second() =>
                        "literal with { and }";
                }
            }
            // outside
            """)!;
        Assert.That(ranges.Ranges, Is.EqualTo(new[]
        {
            new CSharpScopeRange(DeclarationKind.Type, "Outer", 2, 2, 14, -1),
            new CSharpScopeRange(DeclarationKind.Type, "Inner", 4, 4, 13, 0),
            new CSharpScopeRange(DeclarationKind.Method, "First", 6, 7, 10, 1),
            new CSharpScopeRange(DeclarationKind.Method, "Second", 11, 11, 12, 1)
        }));
        foreach (var (line, expected) in new (int, string?)[]
        {
            (2, null), (3, "Outer"), (4, "Outer"), (5, "Outer.Inner"),
            (7, "Outer.Inner"), (8, "Outer.Inner › First"), (10, "Outer.Inner › First"),
            (11, "Outer.Inner"), (12, "Outer.Inner › Second"), (13, "Outer.Inner"),
            (14, "Outer"), (15, null), (8, "Outer.Inner › First"), (2, null)
        })
        {
            var index = ranges.Find(line);
            Assert.That(index < 0 ? null : ranges.Format(index), Is.EqualTo(expected), $"line {line}");
        }
    }

    [Test]
    public void CommentsRegionsAndAbstractMembersDoNotCreateFalseScopes()
    {
        var ranges = Read("namespace Root {\r\n#region section\r\ninterface I {\r\n"
            + "/* #if ignored: { } */\r\nvoid Abstract();\r\n}\r\n#endregion\r\n"
            + "enum Choice\r\n{\r\nA\r\n}\r\n}")!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "I", "Choice" }));
        Assert.That(ranges.Format(ranges.Find(5)), Is.EqualTo("I"));
        Assert.That(ranges.Format(ranges.Find(10)), Is.EqualTo("Choice"));
    }

    [TestCase("class C { void M() {")]
    [TestCase("class C {}\n#   if DEBUG\nclass Other {}\n#endif")]
    [TestCase("class C {}\n#endif")]
    [TestCase("class C { int M() => ; }")]
    public void RejectsUncertainStructure(string source) => Assert.That(Read(source), Is.Null);

    [Test]
    public void CharacterAndScopeLimitsAreInclusive()
    {
        const string header = "class C {}\n";
        var atCharacterLimit = header.PadRight(CSharpScopeRanges.MaximumChars);
        Assert.That(Read(atCharacterLimit)?.Ranges, Has.Count.EqualTo(1));
        Assert.That(Read(atCharacterLimit + " "), Is.Null);
        var atScopeLimit = string.Concat(Enumerable.Repeat(header, CSharpScopeRanges.MaximumRanges));
        Assert.That(Read(atScopeLimit)?.Ranges, Has.Count.EqualTo(CSharpScopeRanges.MaximumRanges));
        Assert.That(Read(atScopeLimit + header.TrimEnd()), Is.Null);
    }

    [Test]
    public void CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read("class C {}", cancellation.Token));
    }

    private static CSharpScopeRanges? Read(string text, CancellationToken ct = default)
    {
        using var lease = new DocumentReadLease(new StringTextSource(text), static () => { });
        return CSharpScopeRanges.Read(lease, ct);
    }
}
