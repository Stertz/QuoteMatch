using QuoteMatch.Desktop.Preferences;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task ProductionWorkspace_StartsWithoutAnUnrelatedDemonstrationAnalysis()
    {
        await session.Session.Dispatch(() =>
        {
            using WorkspaceViewModel model = new(new PreferenceStore(Path.Combine(_directory, "preferences.json")), new SqliteImportStore(_directory));
            Assert.Empty(model.Appearance.Summary.Items);
            Assert.Empty(model.Appearance.VisibleItems);
            Assert.Equal(0, model.Appearance.Summary.SelectedTotal);
            Assert.Equal("pt-BR", model.Strings.Culture.Name);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
