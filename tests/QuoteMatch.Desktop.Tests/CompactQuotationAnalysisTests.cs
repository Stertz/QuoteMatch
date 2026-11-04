using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplierTotals_OrderByComparablePriceBeforeUnmatchedExtras(bool missingExtraTotal)
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic supplier ordering", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var first = AnalysisProposal(quote.Id, "A", 100, 2);
            first = first with { Review = [.. first.Review, first.Review[0] with { CaptureId = Guid.NewGuid(), ProductCode = "EXTRA", ReportedTotal = missingExtraTotal ? null : 5000 }] };
            var second = AnalysisProposal(quote.Id, "B", 100, 3);
            foreach (var proposal in new[] { first, second }) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            var input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with
            {
                Links = new[] { first, second }.Select(proposal => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, "manual:wire", "Synthetic wire", "MT", 1, null, DateTime.UtcNow)).ToArray()
            });
            QuotationAnalysisViewModel model = new(new Localizer(), store, new SyntheticProductLookup()); await model.LoadAsync(quote.Id);
            Assert.Equal(["A", "B"], model.SupplierTotals.Select(total => total.Supplier));
            Assert.Equal([200m, 300m], model.SupplierTotals.Select(total => total.Value.CommonTotal));
            Assert.Equal([missingExtraTotal ? null : 5200m, 300m], model.SupplierTotals.Select(total => total.Value.QuotedTotal));
            input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with { Links = [] }); await model.LoadAsync(quote.Id);
            Assert.Equal(["B", "A"], model.SupplierTotals.Select(total => total.Supplier));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompactComparison_ExpandsByClickEditsByDoubleClickAndKeyboardAndShowsSupplierTotals()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic compact comparison", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposals = new[] { AnalysisProposal(quote.Id, "A", 1500, 8.2m), AnalysisProposal(quote.Id, "B", 2000, 7.8m), AnalysisProposal(quote.Id, "C", 1500, 8m) };
            foreach (var proposal in proposals) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            MappingLookup lookup = new();
            var product = lookup.Product with { LastPurchaseDate = new DateOnly(2026, 9, 10), LastPurchaseUnitPrice = 8.1m };
            var input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with
            {
                Links = proposals.Select(proposal => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, ErpProductIdentity.GroupKey(product), ErpProductIdentity.GroupName(product), "MT", 1, product, DateTime.UtcNow)).ToArray(),
                Demands = [new(ErpProductIdentity.GroupKey(product), "MT", 1500)]
            });
            QuotationAnalysisViewModel model = new(new Localizer(), store, lookup, store); await model.LoadAsync(quote.Id);
            QuotationAnalysisView view = new() { DataContext = model }; Window owner = new() { Width = 1100, Height = 740, Content = view }; owner.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var row = Assert.Single(model.Groups);
                Assert.False(row.IsExpanded); Assert.Equal("B", row.LowestSupplier);
                Assert.Equal("Synthetic cable", row.Name);
                Assert.Equal("R$ 7,8000/MT", row.BestPrice); Assert.Equal("R$ 8,1000/MT", row.LastPurchase);
                Assert.Contains("123", row.LastPurchaseHelp); Assert.Equal(["B", "C", "A"], row.Offers.Select(offer => offer.Supplier));
                Assert.Equal(["C", "A", "B"], model.SupplierTotals.Select(total => total.Supplier));
                Assert.Equal(["R$ 12.000,00", "R$ 12.300,00", "R$ 15.600,00"], model.SupplierTotals.Select(total => total.Total));
                Assert.Equal(["0,00% · menor pacote", "+2,50% mais caro", "+30,00% mais caro"], model.SupplierTotals.Select(total => total.Difference));
                Assert.Equal(["Última compra: -1,23%", "Última compra: +1,23%", "Última compra: -3,70%"], model.SupplierTotals.Select(total => total.PurchaseVariation));
                Assert.True(model.SupplierTotals[1].IsPurchaseIncrease); Assert.True(model.SupplierTotals[2].IsPurchaseDecrease);
                Assert.Contains("R$ 16.200,00", model.SupplierTotals[2].PurchaseVariationHelp);
                Assert.Contains("2.000 MT", model.SupplierTotals[2].PurchaseVariationHelp);
                var footer = view.FindControl<ItemsControl>("SupplierTotalsList")!;
                var supplierLabels = model.SupplierTotals.Select(total => footer.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == total.Supplier)).ToArray();
                Assert.True(supplierLabels[0].TranslatePoint(default, owner)!.Value.X < supplierLabels[1].TranslatePoint(default, owner)!.Value.X);
                Assert.True(supplierLabels[1].TranslatePoint(default, owner)!.Value.X < supplierLabels[2].TranslatePoint(default, owner)!.Value.X);
                var historyHelp = view.FindControl<ItemsControl>("SupplierTotalsList")!.GetVisualDescendants().OfType<QuoteMatch.Desktop.Controls.HelpLabel>()
                    .Single(help => help.Label == "Última compra: -3,70%");
                Assert.True(historyHelp.IsEffectivelyVisible); Assert.Contains("decrease", historyHelp.Classes);
                Assert.InRange(historyHelp.FindAncestorOfType<ScrollViewer>()!.Extent.Height, 1, 112);
                var cheapest = row.Offers[0]; Assert.Equal("-3,70%", cheapest.PurchaseVariation); Assert.True(cheapest.IsPurchaseDecrease);
                Assert.Equal("+1,23%", row.Offers[^1].PurchaseVariation); Assert.True(row.Offers[^1].IsPurchaseIncrease);
                Assert.Contains("8,1/MT", cheapest.PurchaseVariationHelp);
                var group = view.FindControl<ListBox>("AnalysisGroupList")!.ContainerFromIndex(0)!;
                var header = group.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("comparisonToggle"));
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "⚙"));
                Assert.Equal(model.Strings["ProductConfigurationHint"], Avalonia.Automation.AutomationProperties.GetHelpText(header));
                double collapsed = group.Bounds.Height;
                Assert.InRange(collapsed, 40, 60);
                Point point = header.TranslatePoint(new Point(60, 20), owner)!.Value;
                owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                Assert.True(row.IsExpanded); Assert.InRange(group.Bounds.Height - collapsed, 110, 200);
                Assert.Equal(3, group.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("comparisonOffer") && border.IsEffectivelyVisible));
                owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                var editor = Assert.IsType<QuotationItemEditorWindow>(Assert.Single(owner.OwnedWindows));
                Assert.Equal("B", model.SelectedOffer!.Supplier);
                Assert.NotNull(editor.FindControl<NumericUpDown>("MappingConversionInput")); editor.Close();
                if (!row.IsExpanded) { model.ToggleGroupCommand.Execute(row); Dispatcher.UIThread.RunJobs(); }
                var higher = group.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("comparisonOffer") && border.DataContext is AnalysisOfferRow { Supplier: "A" });
                Assert.Equal(model.Strings["ProductConfigurationHint"], ToolTip.GetTip(higher));
                var offerPoint = higher.TranslatePoint(new Point(60, 12), owner)!.Value;
                owner.MouseMove(offerPoint); owner.MouseDown(offerPoint, MouseButton.Left); owner.MouseUp(offerPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                Assert.Empty(owner.OwnedWindows);
                owner.MouseDown(offerPoint, MouseButton.Left); owner.MouseUp(offerPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                editor = Assert.IsType<QuotationItemEditorWindow>(Assert.Single(owner.OwnedWindows));
                Assert.Equal("A", model.SelectedOffer!.Supplier); Assert.Same(model, editor.DataContext); editor.Close();
                higher.Focus(); owner.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.None, null);
                owner.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs();
                editor = Assert.IsType<QuotationItemEditorWindow>(Assert.Single(owner.OwnedWindows));
                Assert.Equal("A", model.SelectedOffer!.Supplier); editor.Close();
                model.ToggleGroupCommand.Execute(row); Dispatcher.UIThread.RunJobs(); Assert.False(row.IsExpanded);
                Assert.Equal(collapsed, group.Bounds.Height);
                model.SearchText = "cable 123"; Assert.Single(model.Groups);
                model.SearchText = "no matching product"; Assert.Empty(model.Groups); Assert.Equal(3, model.SupplierTotals.Count);
                Assert.Equal("R$ 12.000,00", model.SupplierTotals[0].Total);
                Assert.Equal(1, (await store.GetAnalysisInputAsync(quote.Id)).State.Version);
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null, "MT")]
    [InlineData(0, "MT")]
    [InlineData(8, "KG")]
    public async Task CompactComparison_LeavesMissingZeroOrIncompatiblePurchaseVariationsUnavailable(int? price, string historyUnit)
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic history", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quote.Id, "A", 100, 8);
            await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            MappingLookup lookup = new();
            var product = lookup.Product with { LastPurchaseUnitPrice = price, LastPurchaseUnit = historyUnit };
            var input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with { Links = [new(proposal.Id, proposal.Review[0].CaptureId, ErpProductIdentity.GroupKey(product), ErpProductIdentity.GroupName(product), "MT", 1, product, DateTime.UtcNow)] });
            QuotationAnalysisViewModel model = new(new Localizer(), store, lookup); await model.LoadAsync(quote.Id);
            var row = Assert.Single(model.Offers);
            Assert.Equal("—", row.PurchaseVariation); Assert.False(row.IsPurchaseIncrease); Assert.False(row.IsPurchaseDecrease);
            Assert.Equal(model.Strings["PurchaseVariationUnavailable"], row.PurchaseVariationHelp);
            var total = Assert.Single(model.SupplierTotals);
            Assert.Equal("Última compra: indisponível", total.PurchaseVariation);
            Assert.False(total.IsPurchaseIncrease); Assert.False(total.IsPurchaseDecrease);
            Assert.Equal(model.Strings["SupplierPurchaseUnavailable"], total.PurchaseVariationHelp);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
