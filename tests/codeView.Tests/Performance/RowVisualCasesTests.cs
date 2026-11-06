using System.Globalization;
using CodeView.Documents;
using CodeView.Presentation;

namespace CodeView.Tests.Performance;

public sealed class RowVisualCasesTests
{
    [Test]
    public void FixedReferencesAreSmallUniqueAndAlwaysUseScalarBoundaries()
    {
        Assert.That(RowVisualCases.All.Count, Is.EqualTo(12));
        Assert.That(RowVisualCases.All.Select(c => c.Name).Distinct().Count(), Is.EqualTo(12));
        foreach (var c in RowVisualCases.All)
        {
            Assert.That(c.Text.Length, Is.LessThan(4096));
            Assert.That(c.Text.Contains('\r') || c.Text.Contains('\n'), Is.False);
            Assert.That(c.Start, Is.InRange(0, c.Text.Length));
            Assert.That(c.Start == 0 || c.Start == c.Text.Length || !char.IsLowSurrogate(c.Text[c.Start]), Is.True);
            Assert.That(c.Columns, Is.InRange(2, 16384));
        }
    }

    [Test]
    public void ScalarMappingAndOriginalCopyRemainExactForEveryComparison()
    {
        foreach (var c in RowVisualCases.All)
        {
            using var builder = new DocumentBuilder();
            builder.Append(c.Text.AsMemory());
            using var handle = builder.Complete();
            using var source = new DocumentDisplaySession(handle);
            source.SetViewport(1, c.Start);
            var rows = source.CreateRowProjection(1, c.Columns);
            var row = rows.Rows[0];
            Assert.That(rows.Text, Is.EqualTo(c.Text.Substring((int)row.SourceOffset, row.Length)));
            for (var i = 0; i <= rows.Text.Length; i++)
            {
                if (i > 0 && i < rows.Text.Length && char.IsHighSurrogate(rows.Text[i - 1]) && char.IsLowSurrogate(rows.Text[i])) continue;
                Assert.That(rows.TrySourcePosition(i, out var offset), Is.True, c.Name);
                Assert.That(offset, Is.EqualTo(row.SourceOffset + i));
                Assert.That(rows.TryDisplayPosition(offset, out var reverse) && reverse == i, Is.True);
            }
            source.SelectAll();
            using var copy = source.BeginCopy();
            Assert.That(copy.ReadText(CancellationToken.None), Is.EqualTo(c.Text));
        }
    }

    [Test]
    public void ReferencesContainKnownScalarSafeButGraphemeUnsafeCuts()
    {
        foreach (var name in new[] { "combining-left", "zwj-left", "regional-left", "hangul-left", "indic-left" })
        {
            var c = RowVisualCases.All.Single(c => c.Name == name);
            var boundaries = StringInfo.ParseCombiningCharacters(c.Text).ToHashSet();
            Assert.That(boundaries.Contains(c.Start), Is.False, name);
        }
        var whole = RowVisualCases.All.Single(c => c.Name == "unicode-full-control");
        Assert.That(whole.Start, Is.Zero);
        Assert.That(whole.Columns, Is.GreaterThanOrEqualTo(whole.Text.Length));
    }
}
