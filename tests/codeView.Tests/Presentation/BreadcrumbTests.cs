using CodeView.Presentation;

namespace CodeView.Tests.Presentation;

internal sealed class BreadcrumbTests
{
    [Test]
    public void FormatsWorkspaceAndKeepsSameNamedTabsDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), "shop");
        Assert.That(MainWindowViewModel.FormatBreadcrumb(root, Path.Combine(root, "決済", "Gateway.cs")),
            Is.EqualTo("shop / 決済 / Gateway.cs"));
        Assert.That(MainWindowViewModel.FormatBreadcrumb(root, Path.Combine(root, "別名", "Gateway.cs")),
            Is.EqualTo("shop / 別名 / Gateway.cs"));
    }

    [Test]
    public void FallsBackWhenWorkspaceOrFileIsMissingOrOutsideWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "shop");
        var other = Path.Combine(root + "-outside", "Source.cs");
        Assert.That(MainWindowViewModel.FormatBreadcrumb("", ""), Is.EqualTo("codeView"));
        Assert.That(MainWindowViewModel.FormatBreadcrumb(root, ""), Is.EqualTo("shop"));
        Assert.That(MainWindowViewModel.FormatBreadcrumb(root, other), Is.EqualTo("shop-outside / Source.cs"));
        Assert.That(MainWindowViewModel.FormatBreadcrumb("", other), Is.EqualTo("shop-outside / Source.cs"));
    }
}
