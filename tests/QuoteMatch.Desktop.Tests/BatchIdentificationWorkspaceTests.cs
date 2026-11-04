using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuoteMatch.Application.Integration;
using QuoteMatch.Desktop.Localization;
using QuoteMatch.Desktop.ViewModels;
using QuoteMatch.Desktop.Views;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Desktop.Tests;

public sealed partial class WorkspaceTests
{
    [Theory]
    [InlineData(0, "Identificar produto")]
    [InlineData(1, "Confirmar conversão")]
    [InlineData(2, "Selecionar produto")]
    public async Task InternalBatchIdentification_OpensAnEditableBatchAndClosesAfterConfirmingSelectedRows(int candidateCount, string expected)
    {
        await session.Session.Dispatch(async () =>
        {
            var store = new SqliteImportStore(_directory); await store.InitializeAsync();
            var now = DateTime.UtcNow;
            var quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic internal-code batch", "", now, now, QuotationState.Draft, 0));
            var proposal = AnalysisProposal(quote.Id, "Synthetic supplier", 2, 3.125m, "un");
            proposal = proposal with
            {
                FileName = "synthetic.xlsx",
                Template = proposal.Template with { SourceFormat = ImportSourceFormat.Excel, CodeKind = ProductCodeKind.Internal },
                Review = [proposal.Review[0] with { ProductCode = "00.1-U" }, proposal.Review[0] with { CaptureId = Guid.NewGuid(), ProductCode = "00.2-U", Description = "Synthetic second product" }]
            };
            await store.SaveProposalAsync(proposal, [1], TestContext.Current.CancellationToken);
            var lookup = new InternalBatchLookup(candidateCount);
            var preferences = CreateStore();
            var model = new QuotationAnalysisViewModel(new Localizer(), store, lookup, store, preferences); await model.LoadAsync(quote.Id);
            var view = new QuotationAnalysisView { DataContext = model };
            Window owner = new() { Content = view, Width = 1100, Height = 740 }; owner.Show();
            try
            {
                model.SelectedGroup = model.Groups[^1]; model.SelectedGroup.IsExpanded = true;
                Assert.All(model.Groups, group => Assert.Equal("Identificar produto", group.Detail));
                var selectedGroup = model.SelectedGroup;
                string selected = model.SelectedOffer!.Offer.SourceKey;
                var button = view.FindControl<Button>("ReviewQuotationProducts")!;
                button.Focus(); owner.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                owner.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                while (model.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(["00.1-U", "00.2-U"], lookup.Requests.Order());
                Assert.All(model.Groups, group => Assert.Equal(expected, group.Detail));
                Assert.Same(selectedGroup, model.SelectedGroup); Assert.True(model.SelectedGroup!.IsExpanded);
                Assert.All(model.Groups, group => Assert.Equal("Synthetic supplier", group.LowestSupplier));
                Assert.Empty((await store.GetAnalysisInputAsync(quote.Id)).State.Links);
                var editor = Assert.IsType<ProductReviewWindow>(Assert.Single(owner.OwnedWindows));
                var batch = Assert.IsType<ProductReviewViewModel>(editor.DataContext);
                Assert.Equal(2, batch.Rows.Count); Assert.False(batch.RememberMappings);
                if (candidateCount == 0)
                {
                    Assert.False(batch.CanSave); Assert.All(batch.Rows, row => Assert.False(row.IsSelected));
                    Assert.Equal(selected, model.SelectedOffer!.Offer.SourceKey); editor.Close();
                }
                else
                {
                    var first = batch.Rows[0];
                    Assert.All(batch.Rows, row => Assert.Equal(candidateCount, row.Candidates.Count));
                    if (candidateCount == 1)
                    {
                        Assert.True(batch.CanSave); Assert.All(batch.Rows, row => Assert.Equal(2m, row.SystemQuantity));
                        var quantity = editor.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "ReviewSystemQuantity" && ReferenceEquals(control.DataContext, first));
                        quantity.Value = 5m; Dispatcher.UIThread.RunJobs();
                        Assert.Equal(2.5m, first.ConversionFactor); Assert.Equal("R$ 1,2500/un", first.SystemPrice);
                        first.ConversionFactor = 0; Assert.False(batch.CanSave); Assert.True(first.HasValidation);
                        first.ConversionFactor = 2.5m; Assert.True(batch.CanSave);
                    }
                    else
                    {
                        Assert.False(batch.CanSave); first.SelectedCandidate = first.Candidates[1];
                        Assert.True(batch.CanSave); Assert.False(batch.Rows[1].IsSelected);
                    }
                    editor.FindControl<CheckBox>("RememberBatchMappings")!.IsChecked = true; Dispatcher.UIThread.RunJobs();
                    Assert.False(preferences.Load().RememberProductMappings);
                    editor.FindControl<Button>("ConfirmProductReview")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    while (batch.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                    Assert.True(batch.IsSaved, batch.Status); Assert.Empty(owner.OwnedWindows);
                    var saved = (await store.GetAnalysisInputAsync(quote.Id)).State;
                    Assert.Equal(1, saved.Version); Assert.Equal(candidateCount == 1 ? 2 : 1, saved.Links.Count);
                    Assert.All(saved.Links, link => Assert.Equal(ProductGroupingVersion.ExactProductCode, link.GroupingVersion));
                    Assert.True(preferences.Load().RememberProductMappings);
                    var source = SupplierProductReference.From(proposal, first.Item);
                    Assert.Equal(first.ConversionFactor, (await store.FindMappingAsync("synthetic-catalog", source, TestContext.Current.CancellationToken))!.ConversionFactor);
                    Assert.All(model.Groups.Where(group => group.Group.IsLinked), group => Assert.Equal("R$ 2,5000/un", group.LastPurchase));
                    var next = new QuotationAnalysisViewModel(new Localizer(), store, lookup, store, preferences); await next.LoadAsync(quote.Id);
                    Assert.True(next.CreateProductReview().RememberMappings);
                    Assert.Equal(1, (await store.GetAnalysisInputAsync(quote.Id)).State.Version);
                    if (candidateCount == 2)
                    {
                        lookup.Fail = true; button.Focus(); owner.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                        owner.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                        while (model.IsBusy) await Task.Yield(); Dispatcher.UIThread.RunJobs();
                        Assert.Equal(model.Strings["ErpErrorQueryFailed"], model.Status);
                    }
                    Assert.Empty(owner.OwnedWindows);
                }
            }
            finally { owner.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private sealed class InternalBatchLookup(int count) : IErpProductLookup, IErpProductSearch
    {
        public List<string> Requests { get; } = [];
        public bool Fail { get; set; }
        public Task<string> GetCatalogIdentityAsync(CancellationToken token) => Task.FromResult("synthetic-catalog");
        public Task<IReadOnlyList<ErpProductCandidate>> FindByProductCodeAsync(string code, CancellationToken token)
        {
            Requests.Add(code);
            if (Fail) throw new ErpException(ErpError.QueryFailed);
            return Task.FromResult<IReadOnlyList<ErpProductCandidate>>(Enumerable.Range(0, count).Select(index => new ErpProductCandidate(code,
                "Synthetic " + code, "Shared similarity", false, "brand", "Synthetic brand", "ALT-" + index, null, "", null, 2.5m,
                "un", "un", "synthetic-catalog", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))).ToArray());
        }
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string code, CancellationToken token) => throw new InvalidOperationException("Internal identification must not query alternative codes.");
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string code, CancellationToken token) => throw new InvalidOperationException("Internal identification must not query similarities.");
        public Task<IReadOnlyList<ErpProductCandidate>> SearchByNameAsync(string name, CancellationToken token) => throw new InvalidOperationException("Internal identification must not identify by description.");
    }
}
