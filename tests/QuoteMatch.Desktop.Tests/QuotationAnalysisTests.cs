using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    private static SavedProposal AnalysisProposal(Guid quotationId, string supplier, decimal quantity, decimal price, string unit = "MT", int count = 1) =>
        new(Guid.NewGuid(), DateTime.UtcNow, "synthetic.pdf", "synthetic-hash", new PdfTemplate { Name = "Synthetic", SupplierName = supplier },
            new([], "Synthetic"), new([], [], null, null), Enumerable.Range(1, count).Select(index =>
            new ReviewedItem(Guid.NewGuid(), $"{supplier}-{index:000}", $"Synthetic wire {index:000}", quantity, price, quantity * price, unit, false, "")).ToArray())
        { QuotationId = quotationId };

    [Fact]
    public async Task ImportedAnalysis_NavigatesFiftyGroupsAndRecalculatesWithoutChangingSupplierTotals()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            Quotation quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            SavedProposal[] proposals = [AnalysisProposal(quote.Id, "A", 10, 2.5m, count: 50), AnalysisProposal(quote.Id, "B", 20, 1m, count: 50), AnalysisProposal(quote.Id, "C", 10, 1.5m, count: 50)];
            foreach (SavedProposal proposal in proposals) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            AnalysisInput input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with
            {
                Links = proposals.SelectMany(proposal => proposal.Review.Select((item, index) => new ItemLink(proposal.Id, item.CaptureId, $"manual:{index:000}", $"Synthetic wire {index + 1:000}", "MT", 1, null, DateTime.UtcNow))).ToArray(),
                Demands = Enumerable.Range(0, 50).Select(index => new GroupDemand($"manual:{index:000}", "MT", 10)).ToArray()
            });
            using WorkspaceViewModel workspace = new(CreateStore(), store); await workspace.InitializeAsync();
            workspace.SelectedQuotation = workspace.Quotations.Single(); await workspace.OpenQuotationCommand.ExecuteAsync(null);
            workspace.ShowAnalysisCommand.Execute(null);
            WorkspaceWindow window = new() { DataContext = workspace, InitializeOnOpen = false, Width = 1100, Height = 740 }; window.Show();
            try
            {
                var analysis = workspace.Analysis;
                Assert.Equal(1, workspace.QuotationTabIndex); Assert.Equal(50, analysis.Groups.Count); Assert.Equal("R$ 750,00", analysis.EstimatedTotal);
                string[] totals = workspace.Proposals.Select(proposal => proposal.Total).Order().ToArray();
                Assert.Contains("R$ 1.250,00", totals); Assert.All(workspace.Proposals, proposal => Assert.Equal(50, proposal.ItemCount));
                var view = window.GetLogicalDescendants().OfType<QuotationAnalysisView>().First(view => view.IsEffectivelyVisible);
                ListBox list = view.FindControl<ListBox>("AnalysisGroupList")!;
                list.SelectedItem = analysis.Groups[^1]; list.ScrollIntoView(list.SelectedItem); Dispatcher.UIThread.RunJobs();
                Assert.True(list.Bounds.Height >= 250, $"The compact analysis list has only {list.Bounds.Height} pixels available.");
                Assert.Equal("Synthetic wire 050", analysis.SelectedGroup!.Name); Assert.Equal(3, analysis.Offers.Count);
                Assert.False(analysis.SelectedGroup!.IsExpanded);
                analysis.ToggleGroupCommand.Execute(analysis.SelectedGroup);
                Assert.True(analysis.SelectedGroup.IsExpanded);
                Assert.DoesNotContain(analysis.Offers, offer => offer.ErpDetail == workspace.Strings["AnalysisUnlinked"]);
                Assert.NotEqual(workspace.Strings["ImportWorking"], analysis.Status);
                Assert.Equal("C", analysis.SelectedGroup.Group.Winner!.SupplierName); Assert.Equal(1m, analysis.SelectedGroup.Group.LowestUnitPrice);
                Assert.Equal(15m, analysis.SelectedGroup.Group.WinnerCost);
                Assert.Equal("Excedente pago: 10 MT", analysis.Offers.Single(offer => offer.Supplier == "B").Coverage);
                analysis.RequestedQuantity = 20; await analysis.ApplyDemandCommand.ExecuteAsync(null);
                Assert.Equal("B", analysis.SelectedGroup!.Group.Winner!.SupplierName); Assert.Equal("R$ 755,00", analysis.EstimatedTotal);
                analysis.SearchText = "050"; Assert.Single(analysis.Groups); Assert.Equal("R$ 755,00", analysis.EstimatedTotal);
                analysis.SearchText = ""; Assert.Equal(50, analysis.Groups.Count);
                Assert.Equal(totals, workspace.Proposals.Select(proposal => proposal.Total).Order().ToArray());
                Assert.Equal(2, (await store.GetAnalysisInputAsync(quote.Id)).State.Version);
                await workspace.RefreshAsync(); Assert.Equal("R$ 755,00", analysis.EstimatedTotal);
                workspace.RequestFinalizeCommand.Execute(null); await workspace.ConfirmActionCommand.ExecuteAsync(null);
                Assert.False(analysis.CanEdit); Assert.Equal("R$ 755,00", analysis.EstimatedTotal);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Analysis_TotalOverflowIsReportedWithoutCrashingOrDisplayingZero()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            Quotation quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic limits", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            SavedProposal[] proposals = [AnalysisProposal(quote.Id, "A", 1, decimal.MaxValue), AnalysisProposal(quote.Id, "B", 1, 1)];
            foreach (SavedProposal proposal in proposals) await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            AnalysisInput input = await store.GetAnalysisInputAsync(quote.Id);
            await store.SaveAnalysisAsync(input.State with
            {
                Links = proposals.Select(proposal => new ItemLink(proposal.Id, proposal.Review[0].CaptureId, "manual:" + proposal.Template.SupplierName, proposal.Template.SupplierName, "MT", 1, null, DateTime.UtcNow)).ToArray(),
                Demands = [new("manual:A", "MT", 1), new("manual:B", "MT", 1)]
            });
            QuotationAnalysisViewModel model = new(new Localizer(), store, new SyntheticProductLookup()); await model.LoadAsync(quote.Id);
            Exception? error = Record.Exception(() => _ = model.EstimatedTotal);
            Assert.Null(error);
            Assert.NotEqual("R$ 0,00", model.EstimatedTotal);
            Assert.Equal(model.Strings["AnalysisTotalUnavailable"], model.EstimatedTotal);
            Assert.Equal(["B", "A"], model.SupplierTotals.Select(total => total.Supplier));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ErpMapping_RequiresExplicitFactorAndKeepsBaseUnitHistoryOffline()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            Quotation quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            SavedProposal first = AnalysisProposal(quote.Id, "A", 2, 180, "BOB"); SavedProposal second = AnalysisProposal(quote.Id, "B", 200, 2);
            await store.SaveProposalAsync(first, [1], TestContext.Current.CancellationToken); await store.SaveProposalAsync(second, [2], TestContext.Current.CancellationToken);
            SyntheticProductLookup erp = new(); Localizer strings = new(); QuotationAnalysisViewModel model = new(strings, store, erp);
            await model.LoadAsync(quote.Id); Assert.Equal(2, model.Groups.Count);
            Assert.True(model.CanIdentifyPending);
            await model.IdentifyPendingCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "A-001", "B-001" }, erp.CodeRequests.Order());
            Assert.Equal(2, model.Groups.Count); Assert.Empty((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
            model.SelectedGroup = model.Groups.Single(group => group.Group.Offers[0].ProposalId == first.Id);
            Assert.Equal("A-001", model.ProductCodeSearch);
            Assert.Equal(3, model.Candidates.Count);
            model.SimilaritySearch = "Synthetic wire"; await model.SearchErpCommand.ExecuteAsync(null);
            Assert.Equal(3, model.Candidates.Count); Assert.Equal(2, model.Groups.Count);
            model.SelectedCandidate = model.Candidates[1]; Assert.Null(model.ConversionFactor);
            await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Equal(strings["AnalysisErrorInvalidLink"], model.Status);
            Assert.Empty((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
            model.UseErpFactorCommand.Execute(null); Assert.Equal(100m, model.ConversionFactor); Assert.Equal("MT", model.BaseUnit);
            await model.LinkErpCommand.ExecuteAsync(null);
            model.SelectedGroup = model.Groups.Single(group => group.Group.Offers[0].ProposalId == second.Id);
            model.SimilaritySearch = "Synthetic wire"; await model.SearchErpCommand.ExecuteAsync(null); model.SelectedCandidate = model.Candidates[0];
            model.UseBaseUnitCommand.Execute(null); await model.LinkErpCommand.ExecuteAsync(null);
            Assert.Single(model.Groups); Assert.Equal(2, model.Offers.Count);
            Assert.False(model.CanIdentifyPending);
            model.RequestedQuantity = 200; await model.ApplyDemandCommand.ExecuteAsync(null);
            Assert.Equal("A", model.SelectedGroup!.Group.Winner!.SupplierName); Assert.Equal("R$ 360,00", model.EstimatedTotal);
            Assert.Equal(1.8m, model.SelectedGroup.Group.LowestUnitPrice);
            Assert.Contains("200 MT", model.Offers.Single(offer => offer.Supplier == "A").Normalized);
            Assert.Contains("R$ 1,8000/MT", model.Offers.Single(offer => offer.Supplier == "A").Normalized);
            Assert.Contains("R$ 1,8000", model.Offers[0].History); Assert.Contains("MT", model.Offers[0].History);
            erp.Fail = true; await model.SearchErpCommand.ExecuteAsync(null);
            Assert.Equal(strings["ErpErrorConnectionFailed"], model.Status); Assert.Equal("R$ 360,00", model.EstimatedTotal);
            QuotationAnalysisViewModel offline = new(strings, new SqliteImportStore(_directory), erp); await offline.LoadAsync(quote.Id);
            Assert.Equal("R$ 360,00", offline.EstimatedTotal); Assert.Equal(100m, offline.Offers.Single(row => row.Supplier == "A").Offer.Link!.BaseUnitsPerQuotedUnit);
            Assert.Equal(1.8m, offline.Offers[0].Offer.Link!.Product!.LastPurchaseUnitPrice);
            erp.Fail = false; erp.WaitForCancellation = true;
            Task search = model.SearchErpCommand.ExecuteAsync(null); Assert.True(model.IsBusy);
            model.CancelCommand.Execute(null); await search; Assert.False(model.IsBusy); Assert.Equal(strings["ImportCanceled"], model.Status);
            erp.WaitForCancellation = false; model.SimilaritySearch = "Synthetic wire";
            await model.SearchSimilarCommand.ExecuteAsync(null); Assert.Equal(3, model.Candidates.Count);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class SyntheticProductLookup : IErpProductLookup
    {
        public bool Fail { get; set; }
        public bool WaitForCancellation { get; set; }
        public List<string> CodeRequests { get; } = [];
        public async Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string supplierProductCode, CancellationToken token)
        {
            CodeRequests.Add(supplierProductCode);
            return (await FindBySimilarityAsync("Synthetic wire", token)).Select(product => product with { SupplierProductCode = supplierProductCode }).ToArray();
        }
        public async Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken token)
        {
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (Fail) throw new ErpException(ErpError.ConnectionFailed);
            return new[] { ("KM", 1000m), ("RL", 100m), ("MT", 1m) }.Select((unit, index) => new ErpProductCandidate("00123", "Synthetic product", similarityCode, false,
                "1", "Synthetic supplier", "ERP-only-code-" + index, unit.Item2, unit.Item1, new DateOnly(2026, 1, 1), 1.8m, "MT", "RL", "synthetic-catalog", DateTime.UtcNow)).ToArray();
        }
    }
}
