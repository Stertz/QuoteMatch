using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Purchasing;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Persistence;

namespace QuoteMatch.Tests;

public sealed class PurchasePreparationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Purchasing.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static AnalysisInput Input()
    {
        Quotation quote = new(Guid.NewGuid(), "Synthetic study", "", Now, Now, QuotationState.Draft, 1);
        SavedProposal Proposal(string supplier, decimal quantity, decimal price, string unit) => new(Guid.NewGuid(), Now,
            "synthetic.pdf", "synthetic-sha-" + supplier, new PdfTemplate { Id = Guid.NewGuid(), Version = 2, SupplierName = supplier },
            new([], "Synthetic"), new([], [], null, null), [new(Guid.NewGuid(), "000." + supplier, "Synthetic wire", quantity, price, quantity * price, unit, false, "")])
        { QuotationId = quote.Id };
        SavedProposal[] proposals = [Proposal("A", 2, 200, "BOB"), Proposal("B", 250, 1.5m, "MT"), Proposal("C", 100, 2, "MT")];
        ItemLink[] links = proposals.Select((proposal, index) => new ItemLink(proposal.Id, proposal.Review[0].CaptureId,
            "manual:wire", "Synthetic wire", "MT", index == 0 ? 100 : 1, null, Now)).ToArray();
        return new(quote, proposals, new(quote.Id, 1, 1, Now, links, [new("manual:wire", "MT", 200)]));
    }
    private static PurchaseSelection Select(AnalysisInput input, int supplier) => new(input.Proposals[supplier].Id, input.Proposals[supplier].Review[0].CaptureId);

    [Fact]
    public void RetainedChoices_RejectProductOrPackageChangesButKeepTheExactChosenSourceAfterPriceCorrections()
    {
        var input = Input();
        var product = new ErpProductCandidate("product-1", "Synthetic wire", "family", false, "supplier", "Synthetic supplier", "supplier-code",
            100, "BOB", null, null, "MT", "MT", "synthetic-catalog", Now);
        string key = ErpProductIdentity.GroupKey(product);
        input = input with
        {
            State = input.State with
            {
                Links = input.State.Links.Select(link => link with { Product = product, GroupKey = key }).ToArray(),
                Demands = [new(key, "MT", 200)]
            }
        };
        var previous = PurchasePreparationRules.Create(input, [Select(input, 0)], 1, Now);
        var groups = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        var offer = groups[0].Offers[0];
        var corrected = offer with { Item = offer.Item with { UnitPrice = 210, ReportedTotal = 420 } };
        Assert.Equal(Select(input, 0), Assert.Single(PurchasePreparationRules.RetainEligibleSelections([groups[0] with { Offers = [corrected] }], previous)));
        foreach (var replacement in new[]
        {
            offer with { Link = offer.Link! with { Product = product with { ProductCode = "product-2" } } },
            offer with { Link = offer.Link! with { Product = product with { CatalogIdentity = "other-catalog" } } },
            offer with { Link = offer.Link! with { BaseUnitsPerQuotedUnit = 200 } },
            offer with { Item = offer.Item with { Unit = "CX" } },
            offer with { Item = offer.Item with { Color = "Synthetic blue" } }
        })
            Assert.Empty(PurchasePreparationRules.RetainEligibleSelections([groups[0] with { Offers = [replacement] }], previous));
        Assert.Equal(400m, previous.ItemSubtotal); Assert.Equal("product-1", previous.Lines[0].Offer.Link!.Product!.ProductCode);
    }

    [Fact]
    public void RetainedChoices_LeaveMergedChosenGroupsPendingRatherThanSelectingOneOfTheirLots()
    {
        var input = Input();
        var split = input with
        {
            State = input.State with
            {
                Links = input.State.Links.Select((link, index) => link with { GroupKey = "separate:" + index }).ToArray(),
                Demands = [new("separate:0", "MT", 200), new("separate:1", "MT", 200)]
            }
        };
        var previous = PurchasePreparationRules.Create(split, [Select(input, 0), Select(input, 1)], 1, Now);
        var merged = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        Assert.Empty(PurchasePreparationRules.RetainEligibleSelections(merged, previous));
        Assert.Equal(2, previous.Lines.Count); Assert.Equal(775m, previous.ItemSubtotal);
    }

    [Fact]
    public void CompleteSupplierPackage_UsesExistingEligibilityAndRejectsIncompleteOrAmbiguousLots()
    {
        var input = Input();
        var groups = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State);
        var selections = PurchasePreparationRules.SelectSupplierPackage(groups, input.Proposals[0].Id);
        Assert.Equal(Select(input, 0), Assert.Single(selections));
        Assert.Equal(400m, PurchasePreparationRules.Create(input, selections, 1, Now).ItemSubtotal);
        Assert.Throws<PurchaseException>(() => PurchasePreparationRules.SelectSupplierPackage(groups, input.Proposals[2].Id));
        Assert.Throws<PurchaseException>(() => PurchasePreparationRules.SelectSupplierPackage(groups, Guid.NewGuid()));
        Assert.Throws<PurchaseException>(() => PurchasePreparationRules.SelectSupplierPackage([], input.Proposals[0].Id));
        var withoutDemand = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State with { Demands = [] });
        Assert.Equal(Select(input, 0), Assert.Single(PurchasePreparationRules.SelectSupplierPackage(withoutDemand, input.Proposals[0].Id)));
        Guid duplicateId = Guid.NewGuid();
        var duplicateOffer = groups[0].Offers[0] with { Item = groups[0].Offers[0].Item with { CaptureId = duplicateId }, Link = groups[0].Offers[0].Link! with { CaptureId = duplicateId } };
        var duplicate = groups[0] with { Offers = [.. groups[0].Offers, duplicateOffer] };
        Assert.Throws<PurchaseException>(() => PurchasePreparationRules.SelectSupplierPackage([duplicate], input.Proposals[0].Id));
    }
    private static PreparePurchaseRequest Request(AnalysisInput input, int version = 0, params PurchaseSelection[] choices) =>
        new(input.Quotation.Id, input.Quotation.Revision, input.State.Version, version, choices);

    [Fact]
    public void ExplicitChoice_PreservesWholeLotPriceConversionEvidenceAndDemand()
    {
        AnalysisInput input = Input();
        Assert.Equal("B", Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, input.State)).Winner!.SupplierName);
        PurchasePreparation preparation = PurchasePreparationRules.Create(input, [Select(input, 0)], 1, Now);
        PurchaseLine line = Assert.Single(preparation.Lines);
        Assert.Equal(400m, preparation.ItemSubtotal); Assert.Equal(0, preparation.PendingGroupCount);
        Assert.Equal(200m, line.RequestedQuantity); Assert.Equal(200m, line.Offer.BaseQuantity); Assert.Equal(2m, line.Offer.BaseUnitPrice);
        Assert.Equal(2m, line.Offer.Item.Quantity); Assert.Equal(200m, line.Offer.Item.UnitPrice); Assert.Equal("BOB", line.Offer.Item.Unit);
        Assert.Equal("synthetic-sha-A", line.SourceSha256); Assert.Equal(input.Proposals[0].Template.Id, line.TemplateId); Assert.Equal(2, line.TemplateVersion);
        Assert.Equal(Now, preparation.CreatedAtUtc); Assert.Equal(1, preparation.AnalysisVersion);
    }

    [Fact]
    public void PaidExcessAndTies_AllowExplicitChoiceWithoutPretendingFreeDivisibility()
    {
        AnalysisInput input = Input();
        var excess = PurchasePreparationRules.Create(input, [Select(input, 1)], 1, Now);
        Assert.Equal(375m, excess.ItemSubtotal); Assert.Equal(250m, excess.Lines[0].Offer.BaseQuantity);
        SavedProposal second = input.Proposals[1];
        input = input with { Proposals = [input.Proposals[0], second with { Review = [second.Review[0] with { ReportedTotal = 400m }] }, input.Proposals[2]] };
        Assert.True(Assert.Single(ImportedQuotationAnalysis.Calculate(input.Proposals, input.State)).HasTie);
        Assert.Equal(400m, PurchasePreparationRules.Create(input, [Select(input, 1)], 1, Now).ItemSubtotal);
    }

    [Fact]
    public void InvalidChoices_RejectInsufficientForeignExcludedAndDuplicateGroupItems()
    {
        AnalysisInput input = Input();
        PurchaseSelection[][] invalid = [[Select(input, 2)], [new(Guid.NewGuid(), Select(input, 0).CaptureId)],
            [new(Select(input, 0).ProposalId, Guid.NewGuid())], [Select(input, 0), Select(input, 0)], [Select(input, 0), Select(input, 1)]];
        foreach (var choices in invalid)
            Assert.Equal(PurchaseError.InvalidSelection, Assert.Throws<PurchaseException>(() => PurchasePreparationRules.Create(input, choices, 1, Now)).Code);
        var first = input.Proposals[0];
        input = input with { Proposals = [first with { Review = [first.Review[0] with { IsExcluded = true, ExclusionReason = "Synthetic exclusion" }] }, .. input.Proposals.Skip(1)] };
        Assert.Equal(PurchaseError.InvalidSelection, Assert.Throws<PurchaseException>(() => PurchasePreparationRules.Create(input, [Select(input, 0)], 1, Now)).Code);
    }

    [Fact]
    public void MissingIdentityAndConflictingUnits_CannotBecomePurchaseChoices()
    {
        AnalysisInput original = Input();
        AnalysisState[] invalid = [original.State with { Links = [] },
            original.State with { Links = [original.State.Links[0] with { BaseUnit = "KG" }, .. original.State.Links.Skip(1)] }];
        foreach (AnalysisState state in invalid)
            Assert.Equal(PurchaseError.InvalidSelection, Assert.Throws<PurchaseException>(() => PurchasePreparationRules.Create(original with { State = state }, [Select(original, 1)], 1, Now)).Code);
    }

    [Fact]
    public void MissingDesiredQuantity_PreservesNullAndAllowsTheExplicitWholeLot()
    {
        var input = Input();
        input = input with { State = input.State with { Demands = [] } };
        var preparation = PurchasePreparationRules.Create(input, [Select(input, 2)], 1, Now);
        var line = Assert.Single(preparation.Lines);
        Assert.Null(line.RequestedQuantity);
        Assert.Equal(100m, line.Offer.BaseQuantity);
        Assert.Equal(200m, preparation.ItemSubtotal);
    }

    [Fact]
    public void ClearingChoices_LeavesAnExplicitEmptyRevisionAndPendingGroups()
    {
        var empty = PurchasePreparationRules.Create(Input(), [], 2, Now);
        Assert.Empty(empty.Lines); Assert.Equal(0m, empty.ItemSubtotal); Assert.Equal(1, empty.PendingGroupCount); Assert.Equal(2, empty.Version);
    }

    [Fact]
    public void CombinedSubtotalOverflow_IsRejectedInsteadOfWrappingOrBecomingZero()
    {
        AnalysisInput input = Input();
        input = input with
        {
            Proposals = input.Proposals.Take(2).Select(proposal => proposal with { Review = [proposal.Review[0] with { ReportedTotal = decimal.MaxValue }] }).ToArray(),
            State = input.State with { Links = [input.State.Links[0], input.State.Links[1] with { GroupKey = "manual:other" }], Demands = [.. input.State.Demands, new("manual:other", "MT", 200)] }
        };
        Assert.Equal(PurchaseError.NumericLimit, Assert.Throws<PurchaseException>(() => PurchasePreparationRules.Create(input, [Select(input, 0), Select(input, 1)], 1, Now)).Code);
    }

    private async Task<(SqliteImportStore Store, AnalysisInput Input)> SeedAsync(bool previousSchema = false)
    {
        if (previousSchema)
        {
            Directory.CreateDirectory(_directory);
            await using ImportDbContext context = new(new DbContextOptionsBuilder<ImportDbContext>().UseSqlite(ConnectionString()).Options);
            await context.GetService<IMigrator>().MigrateAsync("AnalysisRevisions", Token);
        }
        SqliteImportStore store = new(_directory);
        if (!previousSchema) await store.InitializeAsync(Token);
        AnalysisInput seed = Input();
        Quotation quote = await store.SaveQuotationAsync(seed.Quotation with { Revision = 0 }, Token);
        foreach (SavedProposal proposal in seed.Proposals) await store.SaveProposalAsync(proposal, [1, 2, 3], Token);
        if (previousSchema)
        {
            quote = (await store.GetQuotationsAsync(Token)).Single().Quotation;
            AnalysisState state = seed.State with { Version = 1, InputsRevision = quote.Revision };
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(state, SqliteImportStore.JsonOptions))!;
            json.AsObject().Remove("QuotationInputsVersion");
            foreach (var link in json["Links"]!.AsArray()) link!.AsObject().Remove("GroupingVersion");
            foreach (var demand in json["Demands"]!.AsArray())
            {
                demand!.AsObject().Remove("Origin"); demand.AsObject().Remove("Sources");
            }
            await using ImportDbContext context = new(new DbContextOptionsBuilder<ImportDbContext>().UseSqlite(ConnectionString()).Options);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AnalysisRevisions (QuotationId, Version, InputsRevision, CreatedAtUtc, DataJson) VALUES ({quote.Id}, {state.Version}, {state.InputsRevision}, {state.CreatedAtUtc}, {json.ToJsonString()})", Token);
            return (store, seed with { Quotation = quote, State = state });
        }
        AnalysisInput loaded = await store.GetAnalysisInputAsync(quote.Id, Token);
        await store.SaveAnalysisAsync(loaded.State with { Links = seed.State.Links, Demands = seed.State.Demands }, Token);
        return (store, await store.GetAnalysisInputAsync(quote.Id, Token));
    }

    [Fact]
    public async Task SavedPreparation_RestartsOfflineAndAppendsFrozenRevisions()
    {
        var (store, input) = await SeedAsync();
        PurchasePreparation first = await store.PreparePurchaseAsync(Request(input, 0, Select(input, 0)), Token);
        PurchasePreparation second = await store.PreparePurchaseAsync(Request(input, 1, Select(input, 1)), Token);
        var loaded = await new SqliteImportStore(_directory).GetPurchaseInputAsync(input.Quotation.Id, Token);
        Assert.Equal(2, loaded.Preparation!.Version); Assert.Equal(375m, loaded.Preparation.ItemSubtotal);
        Assert.Equal(400m, first.ItemSubtotal); Assert.Equal(1, first.Version);
        Assert.Equal(250m, second.Lines[0].Offer.BaseQuantity); Assert.Equal(200m, second.Lines[0].RequestedQuantity);
        Assert.Equal(input.Quotation.Revision, loaded.Analysis.Quotation.Revision);
        await using SqliteConnection connection = new(ConnectionString()); await connection.OpenAsync(Token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DataJson FROM PurchasePreparations WHERE Version = 1";
        var historical = JsonSerializer.Deserialize<PurchasePreparation>((string)(await command.ExecuteScalarAsync(Token))!, SqliteImportStore.JsonOptions)!;
        Assert.Equal(400m, historical.ItemSubtotal); Assert.Equal("synthetic-sha-A", historical.Lines[0].SourceSha256);
    }

    [Fact]
    public async Task ChangesToPreparationAnalysisOrQuotation_RejectStaleWrites()
    {
        var (store, input) = await SeedAsync();
        PreparePurchaseRequest request = Request(input, 0, Select(input, 0));
        await store.PreparePurchaseAsync(request, Token);
        Assert.Equal(PurchaseError.Conflict, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(request, Token))).Code);
        await store.SaveAnalysisAsync(input.State with { Demands = [new("manual:wire", "MT", 250)] }, Token);
        Assert.Equal(PurchaseError.Conflict, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(request with { ExpectedVersion = 1 }, Token))).Code);
        var updated = await store.GetPurchaseInputAsync(input.Quotation.Id, Token);
        Assert.Equal(400m, updated.Preparation!.ItemSubtotal); Assert.Equal(200m, updated.Preparation.Lines[0].RequestedQuantity);
        await store.SaveQuotationAsync(updated.Analysis.Quotation with { Name = "Changed study" }, Token);
        Assert.Equal(PurchaseError.Conflict, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(Request(updated.Analysis, 1, Select(input, 1)), Token))).Code);
        Assert.Equal(PurchaseError.Conflict, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(request with { QuotationId = Guid.NewGuid() }, Token))).Code);
        Assert.Equal(1, (await store.GetPurchaseInputAsync(input.Quotation.Id, Token)).Preparation!.Version);
    }

    [Fact]
    public async Task FinalizationProtectsPreparation_AndDeletionCascadesItsRevisions()
    {
        var (store, input) = await SeedAsync();
        await store.PreparePurchaseAsync(Request(input, 0, Select(input, 0)), Token);
        Quotation finalized = await store.SaveQuotationAsync(input.Quotation with { State = QuotationState.Finalized }, Token);
        var loaded = await store.GetPurchaseInputAsync(finalized.Id, Token);
        Assert.Equal(400m, loaded.Preparation!.ItemSubtotal);
        Assert.Equal(PurchaseError.ReadOnly, (await Assert.ThrowsAsync<PurchaseException>(() => store.PreparePurchaseAsync(Request(loaded.Analysis, 1), Token))).Code);
        await store.DeleteQuotationAsync(finalized.Id, finalized.Revision, Token);
        await using SqliteConnection connection = new(ConnectionString()); await connection.OpenAsync(Token);
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM PurchasePreparations";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task Migration_PreservesPriorAnalysisProposalsAndOriginalBytes()
    {
        var (store, input) = await SeedAsync(previousSchema: true);
        await store.InitializeAsync(Token);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "backups"), "*.db"));
        Assert.Null((await store.GetPurchaseInputAsync(input.Quotation.Id, Token)).Preparation);
        Assert.Equal(3, (await store.GetAnalysisInputAsync(input.Quotation.Id, Token)).Proposals.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetOriginalPdfAsync(input.Proposals[0].Id, Token));
        Assert.Equal(400m, (await store.PreparePurchaseAsync(Request(input, 0, Select(input, 0)), Token)).ItemSubtotal);
    }

    [Fact]
    public async Task CanceledPreparation_DoesNotAppendAnyRevision()
    {
        var (store, input) = await SeedAsync();
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PreparePurchaseAsync(Request(input, 0, Select(input, 0)), canceled.Token));
        Assert.Null((await store.GetPurchaseInputAsync(input.Quotation.Id, Token)).Preparation);
    }

    private string ConnectionString() => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "imports.db") }.ToString();
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
