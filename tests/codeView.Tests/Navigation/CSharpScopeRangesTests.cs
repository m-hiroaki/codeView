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
        Assert.That(ranges.Ranges[ranges.Find(4)].Kind, Is.EqualTo(DeclarationKind.Method));
        Assert.That(ranges.Ranges[ranges.Find(7)].Kind, Is.EqualTo(DeclarationKind.Method));
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
            new StickyScopeRange(DeclarationKind.Type, "Outer", 2, 2, 14, -1),
            new StickyScopeRange(DeclarationKind.Type, "Inner", 4, 4, 13, 0),
            new StickyScopeRange(DeclarationKind.Method, "First", 6, 7, 10, 1),
            new StickyScopeRange(DeclarationKind.Method, "Second", 11, 11, 12, 1)
        }));
        foreach (var (line, expected) in new (int, string?)[]
        {
            (2, null), (3, "Outer"), (4, "Outer"), (5, "Inner"),
            (7, "Inner"), (8, "First"), (10, "First"),
            (11, "Inner"), (12, "Second"), (13, "Inner"),
            (14, "Outer"), (15, null), (8, "First"), (2, null)
        })
        {
            var index = ranges.Find(line);
            Assert.That(index < 0 ? null : ranges.Ranges[index].Name, Is.EqualTo(expected), $"line {line}");
        }
    }

    [Test]
    public void CommentsRegionsAndAbstractMembersDoNotCreateFalseScopes()
    {
        var ranges = Read("namespace Root {\r\n#region section\r\ninterface I {\r\n"
            + "/* #if ignored: { } */\r\nvoid Abstract();\r\n}\r\n#endregion\r\n"
            + "enum Choice\r\n{\r\nA\r\n}\r\n}")!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "I", "Choice" }));
        Assert.That(ranges.Ranges[ranges.Find(5)].Name, Is.EqualTo("I"));
        Assert.That(ranges.Ranges[ranges.Find(10)].Name, Is.EqualTo("Choice"));
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
        var atCharacterLimit = header.PadRight(StickyScopeRanges.MaximumChars);
        Assert.That(Read(atCharacterLimit)?.Ranges, Has.Count.EqualTo(1));
        Assert.That(Read(atCharacterLimit + " "), Is.Null);
        var atScopeLimit = string.Concat(Enumerable.Repeat(header, StickyScopeRanges.MaximumRanges));
        Assert.That(Read(atScopeLimit)?.Ranges, Has.Count.EqualTo(StickyScopeRanges.MaximumRanges));
        Assert.That(Read(atScopeLimit + header.TrimEnd()), Is.Null);
    }

    [Test]
    public void CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read("class C {}", cancellation.Token));
    }

    private static StickyScopeRanges? Read(string text, CancellationToken ct = default)
    {
        using var lease = new DocumentReadLease(new StringTextSource(text), static () => { });
        return StickyScopeRanges.ReadCSharp(lease, ct);
    }
}
