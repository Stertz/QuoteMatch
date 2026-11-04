using Avalonia.Controls;
using Avalonia.Threading;
using QuoteMatch.Application.Importing;
using QuoteMatch.Application.Purchasing;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Pdf;
using QuoteMatch.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task PdfFinancialMapping_ReviewsOptionalItemValuesAndReopensDocumentEvidenceOffline()
    {
        await session.Session.Dispatch(async () =>
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "synthetic-financial.pdf");
            PdfDocumentBuilder builder = new(); var font = builder.AddStandard14Font(Standard14Font.Helvetica); var page = builder.AddPage(PageSize.A4);
            page.AddText("001 Synthetic wire 2 10,00 20,00 IPI 5,00 1,00", 12, new PdfPoint(40, 790), font);
            page.AddText("Freight 12,30", 12, new PdfPoint(40, 750), font);
            await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
            SqliteImportStore store = new(_directory); await store.InitializeAsync();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic financial review", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            using ImportWorkspaceViewModel model = new(new(), store, new PdfImportService(new PdfPigTextExtractor(), store), quotation.Id, quotation.Name);
            await model.InitializeAsync(); model.NewTemplateCommand.Execute(null);
            model.Editor.Name = "Synthetic financial template"; model.Editor.SupplierName = "Synthetic supplier";
            model.Editor.RecordPattern = @"^(?<code>\S+)\s+(?<description>.+?)\s+(?<quantity>[\d.,]+)\s+(?<unitPrice>[\d.,]+)\s+(?<total>[\d.,]+)\s+IPI\s+(?<ipiRate>[\d.,]+)\s+(?<ipiAmount>[\d.,]+)\s*$";
            model.Editor.TableEndPattern = "^Freight"; model.Editor.DocumentFinancialPattern = @"^Freight (?<freight>[\d.,]+)$";
            await model.SaveTemplateCommand.ExecuteAsync(null); await model.PreviewFileAsync(path);
            Assert.True(model.CanSave); Assert.Equal(12.30m, model.DocumentFinancials!.Build().Freight);
            Assert.Contains("12,30", model.FinancialEvidence); Assert.All(model.DocumentFinancials.Fields, row => Assert.True(row.IsReadOnly));
            var item = Assert.Single(model.Items); var rate = Assert.Single(item.Financials.Fields, row => row.Field == FinancialField.IpiRate);
            Assert.Equal("5", rate.Text); rate.Text = "invalid";
            Assert.Equal(model.Strings["FinancialInvalidValues"], model.SelectedReviewWarning);
            await model.ConfirmCommand.ExecuteAsync(null); Assert.Equal(model.Strings["ErrorInvalidReview"], model.Status);
            Assert.Empty(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            rate.Text = "6,25"; await model.ConfirmCommand.ExecuteAsync(null);
            var summary = Assert.Single(await store.GetProposalsAsync(TestContext.Current.CancellationToken));
            var saved = (await store.GetProposalAsync(summary.Id, TestContext.Current.CancellationToken))!;
            Assert.Equal(6.25m, saved.Review[0].Financials.IpiRate); Assert.Equal(5m, saved.Extraction.Items[0].Financials.IpiRate);
            Assert.Equal("5,00", saved.Extraction.Items[0].OriginalFields["ipiRate"]); Assert.Equal(12.30m, saved.Extraction.DocumentFinancials.Values.Freight);
            model.SelectedProposal = summary; await model.OpenProposalCommand.ExecuteAsync(null);
            Assert.True(model.IsConsulting); Assert.Equal(6.25m, model.Items[0].Financials.Build().IpiRate);
            Assert.Null(model.Items[0].Financials.Build().IcmsAmount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PriceSwitch_RequiresACompatibleQueryAndDisplaysItsApprovedValuesBeforeConfirmation()
    {
        await session.Session.Dispatch(async () =>
        {
            SqliteImportStore store = new(_directory); await store.InitializeAsync(); UnifiedErp erp = new(); Localizer strings = new();
            var quotation = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic policy selection", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quotation.Id, "A", 10, 250, "RL"); await store.SaveProposalAsync(proposal, [1]);
            var input = await store.GetAnalysisInputAsync(quotation.Id); string key = ErpProductIdentity.GroupKey(erp.Product);
            var analysis = await store.SaveAnalysisAsync(input.State with { Links = [new(proposal.Id, proposal.Review[0].CaptureId, key, "Synthetic wire", "M", 100, erp.Product, DateTime.UtcNow)], Demands = [new(key, "M", 1000)] });
            await store.PreparePurchaseAsync(new(quotation.Id, input.Quotation.Revision, analysis.Version, 0, [new(proposal.Id, proposal.Review[0].CaptureId)]));
            erp.Order = erp.Order with
            {
                Financials = new() { Freight = 4 },
                OrderTotal = 2604,
                Lines = [erp.Order.Lines[0] with { ProductCode = erp.Product.ProductCode, Quantity = 10, UnitPrice = 260, Total = 2600, ReceivedQuantity = 0, Brand = erp.Product.Brand, Financials = new() }]
            };
            WorkspaceFinancialWriter writer = new(); PurchaseSynchronizationViewModel model = new(strings, store, erp, erp, writer, store);
            await model.LoadAsync(quotation.Id); model.Branch = "branch-1"; model.KindIndex = 1;
            await model.SearchTargetsCommand.ExecuteAsync(null); model.SelectedTarget = model.Targets.Single(); await model.PreviewCommand.ExecuteAsync(null);
            PurchaseSynchronizationWindow window = new() { DataContext = model }; window.Show();
            try
            {
                window.FindControl<Expander>("FinancialPolicies")!.IsExpanded = true;
                model.Differences[0].IsSelected = true;
                window.FindControl<ToggleSwitch>("UpdatePricesInput")!.IsChecked = false; Dispatcher.UIThread.RunJobs();
                Assert.False(model.Differences[0].IsSelected); Assert.False(model.Differences[0].CanSelect);
                Assert.Equal(SynchronizationPriceSource.KeepExisting, model.FinancialPolicy.Prices);
                Assert.False(window.FindControl<ComboBox>("PriceSourceInput")!.IsEnabled);
                model.FinancialPolicies[0].SourceIndex = (int)SynchronizationFinancialSource.Zero;
                await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Equal(strings["OrderErrorFinancialPolicyUnsupported"], model.Status); Assert.False(model.CanConfirm); Assert.Equal(0, writer.Writes);
                writer.SupportsFinancials = true; await model.ReviewSelectionCommand.ExecuteAsync(null);
                Assert.Contains(strings.Money(2600), model.FinancialSummary); Assert.Equal(strings.Money(260, 4), Assert.Single(model.ResultLines).Price);
                Assert.True(model.CanConfirm);
                model.FinancialPolicies[0].SourceIndex = (int)SynchronizationFinancialSource.KeepExisting;
                Assert.False(model.CanConfirm); Assert.False(model.HasSelection);
                model.FinancialPolicies[0].SourceIndex = (int)SynchronizationFinancialSource.Zero;
                await model.ReviewSelectionCommand.ExecuteAsync(null);
                await model.ConfirmWriteCommand.ExecuteAsync(null);
                Assert.Equal(1, writer.Writes); Assert.Equal(strings["OrderStateCommitted"], model.Status);
                Assert.Equal(SynchronizationPriceSource.KeepExisting, writer.Approved!.FinancialPolicy.Prices);
                Assert.Equal(260, writer.Approved.FinancialPreview!.Lines[0].UnitPrice);
                model.KindIndex = 0;
                Assert.True(model.UpdatePrices); Assert.NotEqual((int)SynchronizationPriceSource.KeepExisting, model.PriceSourceIndex);
                Assert.All(model.FinancialPolicies, row => Assert.False(row.CanKeepExisting));
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class WorkspaceFinancialWriter : IPurchaseSynchronizationWriter
    {
        public bool SupportsFinancials { get; set; }
        public int Writes { get; private set; }
        public SynchronizationWritePlan? Approved { get; private set; }
        public Task<SynchronizationWriteProfile> GetProfileAsync(OrderWriteKind kind, CancellationToken token) => Task.FromResult(new SynchronizationWriteProfile("synthetic-financial-script", 112) { Kind = kind, SupportsFinancialPolicies = SupportsFinancials });
        public Task<SynchronizationFinancialPreview> PreviewFinancialsAsync(SynchronizationWritePlan plan, CancellationToken token) => Task.FromResult(new SynchronizationFinancialPreview(
            SynchronizationFinancialStatus.Ready, "synthetic-financial-revision", true, new() { Freight = 0 }, 2600,
            [new(0, plan.Selection.Lines[0].Control, plan.Selection.Lines[0].ProductCode, "RL", "M", 10, 100, 260, 2600, new())], []));
        public Task<SynchronizationAllocation> ReserveAsync(SynchronizationWritePlan plan, CancellationToken token) => throw new InvalidOperationException("This scenario updates an existing order.");
        public Task<OrderWriteReceipt> ExecuteAsync(SynchronizationWritePlan plan, CancellationToken token)
        { Writes++; Approved = plan; return Task.FromResult(new OrderWriteReceipt(plan.OperationId, OrderOperationState.Committed, plan.TargetId) { FinancialRevision = plan.FinancialPreview!.Revision }); }
        public Task<OrderWriteReceipt> ReconcileAsync(SynchronizationOperation operation, CancellationToken token) => throw new InvalidOperationException("No uncertain dispatch is simulated here.");
    }
}
