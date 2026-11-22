using AvaloniaEdit.Document;
using CodeView.Documents;
using CodeView.Navigation;

namespace CodeView.Tests.Navigation;

internal sealed class CppScopeRangesTests
{
    [Test]
    public void ReadsOriginalFunctionAndNamedTypeBoundariesInC()
    {
        var ranges = Read("""
            #line 900 "source.c"
            struct State
            {
                int value;
            };
            enum Flag
            {
                Off,
                On
            };
            int forward(int value);
            int read(
                int value)
            {
                const char *text = "{";
                /* another } */
                return value;
            }
            """)!;
        Assert.That(ranges.Ranges, Is.EqualTo(new[]
        {
            new StickyScopeRange(DeclarationKind.Type, "State", 2, 2, 5, -1),
            new StickyScopeRange(DeclarationKind.Type, "Flag", 6, 6, 10, -1),
            new StickyScopeRange(DeclarationKind.Function, "read", 12, 13, 18, -1),
        }));
        Assert.That(ranges.Find(13), Is.EqualTo(-1));
        Assert.That(ranges.Find(18), Is.EqualTo(2));
    }

    [Test]
    public void ReadsHeaderGuardTemplatesConstructorsAndQualifiedMethods()
    {
        var ranges = Read("""
            #ifndef READER_HPP
            #define READER_HPP
            namespace library {
            template <typename T>
            class Reader {
                T value;
            public:
                Reader(T v) : value{v}
                {
                    const auto text = R"delim(})delim";
                }
                T get() const {
                    auto action = [] { return 0; };
                    return value;
                }
            };
            template<typename T>
            T Reader<T>::read() {
                return {};
            }
            }
            #endif
            """)!;
        Assert.That(ranges.Ranges.Select(range => (range.Name, range.Parent, range.HeaderEndLine, range.EndLine)),
            Is.EqualTo(new[] { ("Reader", -1, 5L, 16L), ("Reader", 0, 8L, 11L), ("get", 0, 12L, 15L),
                ("read", -1, 18L, 20L) }));
    }

    [Test]
    public void RetainsCompleteBranchesAndRecoversAfterAnUnbalancedAlternative()
    {
        var ranges = Read("""
            int before() { return 1; }
            #if A
            int first() {
                return 1;
            }
            #elif B
            int unbalanced() {
            #else
            # if C
            int nested() { return 3; }
            # endif
            #endif
            int after() { return 4; }
            """)!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "before", "first", "nested", "after" }));
        Assert.That(ranges.Find(4), Is.EqualTo(1));
        Assert.That(ranges.Find(7), Is.EqualTo(-1));
    }

    [Test]
    public void SuppressesOnlyEnclosingUncertainScopeAndLeavesIndependentFunctions()
    {
        var ranges = Read("""
            int before() { return 1; }
            int uncertain() {
            #if A
            }
            #endif
                return 2;
            }
            int after() { return 3; }
            """)!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "before", "after" }));
    }

    [Test]
    public void IgnoresMacroReplacementBracesAndDoesNotGuessTheirEffects()
    {
        var ranges = Read("#define CONTENT \\\n { }\n#define CONSTANT 7\n"
            + "int valid() {\n return CONSTANT;\n}\n"
            + "int uncertain() {\n CONTENT\n return 0;\n}\n"
            + "int after() { return 3; }")!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "valid", "after" }));
    }

    [TestCase("#else\nint orphan() {}")]
    [TestCase("#if X\nint unfinished() {}")]
    [TestCase("int incomplete() {")]
    [TestCase("int partial(\n#if X\nint x\n#endif\n) {}")]
    public void RejectsUnrecoverableSource(string text) => Assert.That(Read(text), Is.Null);

    [Test]
    public void RemovesMembersOfATypedScopeWithAMacroName()
    {
        var ranges = Read("""
            #define TYPE Actual
            class TYPE {
            public:
                void hidden() {
                }
            };
            int after() {
                return 0;
            }
            """)!;
        Assert.That(ranges.Ranges.Select(range => range.Name), Is.EqualTo(new[] { "after" }));
    }

    [Test]
    public void BoundsCharactersRangesAndCancellation()
    {
        const string function = "int read() {}\n";
        Assert.That(Read(function.PadRight(StickyScopeRanges.MaximumChars))?.Ranges, Has.Count.EqualTo(1));
        Assert.That(Read(function.PadRight(StickyScopeRanges.MaximumChars + 1)), Is.Null);
        var atLimit = string.Concat(Enumerable.Repeat(function, StickyScopeRanges.MaximumRanges));
        Assert.That(Read(atLimit)?.Ranges, Has.Count.EqualTo(StickyScopeRanges.MaximumRanges));
        Assert.That(Read(atLimit + function), Is.Null);
        Assert.Throws<OperationCanceledException>(() => Read(function, new CancellationToken(true)));
    }

    private static StickyScopeRanges? Read(string source, CancellationToken ct = default)
    {
        using var lease = new DocumentReadLease(new StringTextSource(source), static () => { });
        return CppDeclarationScanner.ReadScopes(lease, ct);
    }
}
