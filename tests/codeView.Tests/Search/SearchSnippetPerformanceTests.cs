using System.Diagnostics;
using CodeView.Contracts;
using CodeView.Search;

namespace CodeView.Tests.Search;

internal sealed class SearchSnippetPerformanceTests
{
    [TestCase(false), TestCase(true), Category("Performance")]
    public void CaptureTenThousandHitsWithinAllocationBudget(bool regex)
    {
        // Construct source outside measurement; no result collection or document/GUI is involved.
        var text = string.Concat(Enumerable.Repeat("// FOO " + new string('a', 192) + "\n", 10000));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var pause = GC.GetTotalPauseDuration();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var count = 0;
        var characters = 0L;
        var valid = true;
        void Inspect(SearchSnippet snippet, TextRange hit)
        {
            valid &= snippet.Text.Length <= 200 && snippet.MatchLength == 3
                && snippet.Text.AsSpan(snippet.MatchStart, snippet.MatchLength).SequenceEqual("FOO")
                && hit.Start.Line == count + 1 && hit.Start.Column == 4;
            characters += snippet.Text.Length;
            count++;
        }
        if (regex)
        {
            var matcher = RegexLineMatcher.Create("foo", new(UseRegex: true)).Value;
            Func<TextRange, bool> consume = hit => { Inspect(matcher.CaptureSnippet(), hit); return true; };
            matcher.Push(text, consume, default);
            matcher.Complete(consume, default);
        }
        else
        {
            var matcher = LiteralMatcher.Create("foo").Value;
            Func<TextRange, bool> consume = hit => { Inspect(matcher.CaptureSnippet(), hit); return true; };
            matcher.Push(text, consume, default);
            matcher.Complete(consume, default);
        }
        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        TestContext.Progress.WriteLine($"regex={regex}; hits={count}; snippetChars={characters}; elapsedMs={watch.Elapsed.TotalMilliseconds:F3}; cpuMs={cpuMs:F3}; allocatedBytes={bytes}; gcPauseMs={pauseMs:F3}");
        Assert.That(valid, Is.True);
        Assert.That(count, Is.EqualTo(10000));
        Assert.That(bytes, Is.LessThanOrEqualTo(8 << 20));
        Assert.That(watch.Elapsed.TotalMilliseconds, Is.LessThanOrEqualTo(1000));
        Assert.That(cpuMs, Is.LessThanOrEqualTo(1000));
    }
}
