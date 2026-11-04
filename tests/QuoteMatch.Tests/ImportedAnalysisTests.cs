using System.Data;
using System.Text;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Erp;
using QuoteMatch.Infrastructure.Persistence;
namespace QuoteMatch.Tests;

public sealed class ImportedAnalysisTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuoteMatch.Analysis.Tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SavedProposal Proposal(string supplier, decimal quantity, decimal price, string unit = "MT", decimal? total = null)
    {
        Guid capture = Guid.NewGuid();
        return new(Guid.NewGuid(), DateTime.UtcNow, "synthetic.pdf", "hash", new PdfTemplate { Name = "Synthetic", SupplierName = supplier }, new([], "Synthetic"), new([], [], null, null),
            [new(capture, "Supplier-specific-code-" + supplier, "Synthetic wire", quantity, price, total ?? quantity * price, unit, false, "")]);
    }
    private static ItemLink Link(SavedProposal proposal, decimal factor = 1, string unit = "MT") => new(proposal.Id, proposal.Review[0].CaptureId, "manual:wire", "Wire", unit, factor, null, DateTime.UtcNow);
    private static AnalysisState State(SavedProposal[] proposals, decimal? demand = 1500) => new(Guid.NewGuid(), 0, 1, DateTime.UtcNow,
        proposals.Select(proposal => Link(proposal)).ToArray(), demand is null ? [] : [new("manual:wire", "MT", demand.Value)]);

    [Fact]
    public void RealOffers_LowestUnitPriceDoesNotOverrideExcessLotCost()
    {
        SavedProposal[] proposals = [Proposal("A", 1500, 8.2m), Proposal("B", 2000, 7.8m), Proposal("C", 1500, 8m)];
        AnalyzedGroup group = Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, State(proposals)));
        Assert.Equal(7.8m, group.LowestUnitPrice); Assert.Equal("C", group.Winner!.SupplierName); Assert.Equal(12000m, group.WinnerCost);
        Assert.Equal(3, group.Offers.Count); Assert.Equal(2000m, group.Offers.Single(offer => offer.SupplierName == "B").BaseQuantity);
    }
    [Fact]
    public void ExplicitConversion_UsesNumericFactorAndReportedTotal_PreservingSourceValues()
    {
        SavedProposal[] proposals = [Proposal("A", 3, 190m, "BOB", 575m), Proposal("B", 300, 2m)];
        AnalysisState state = State(proposals, 300) with { Links = [Link(proposals[0], 100), Link(proposals[1])] };
        AnalyzedGroup group = Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, state));
        AnalyzedOffer normalized = group.Offers[0]; Assert.Equal(300m, normalized.BaseQuantity); Assert.Equal(1.9m, normalized.BaseUnitPrice);
        Assert.Equal(3m, normalized.Item.Quantity); Assert.Equal(190m, normalized.Item.UnitPrice); Assert.Equal(575m, group.WinnerCost);
    }
    [Fact]
    public void ErpLink_CannotReplaceTheKnownProductBaseUnitWithAnotherUnit()
    {
        SavedProposal proposal = Proposal("A", 2, 180, "BOB");
        ErpProductCandidate product = new("00123", "Synthetic product", "Wire", false, "1", "Synthetic supplier", "ERP-code", 100, "RL",
            null, null, "MT", "MT", "synthetic-catalog", DateTime.UtcNow);
        ItemLink link = Link(proposal, 100, "KG") with { Product = product };
        Assert.Equal(AnalysisError.InvalidLink, Assert.Throws<AnalysisException>(() => ImportedQuotationAnalysis.ValidateLink(link, proposal.Review[0])).Code);
    }

    [Fact]
    public void ErpLink_CannotOverrideTheProductSimilarityGroup()
    {
        SavedProposal proposal = Proposal("A", 1, 100, "RL");
        ErpProductCandidate product = new("00123", "Synthetic product", "Wire", false, "1", "Synthetic supplier", "ERP-code", 100, "RL",
            null, null, "MT", "MT", "synthetic-catalog", DateTime.UtcNow);
        ItemLink link = Link(proposal, 100) with { Product = product, GroupKey = "erp:synthetic-catalog:OTHER" };
        Assert.Equal(AnalysisError.InvalidLink, Assert.Throws<AnalysisException>(() => ImportedQuotationAnalysis.ValidateLink(link, proposal.Review[0])).Code);
    }

    [Fact]
    public void UnlinkedTextMatches_NeverBecomeAutomaticEquivalence()
    {
        SavedProposal[] proposals = [Proposal("A", 5, 2), Proposal("B", 5, 1)];
        AnalyzedGroup[] groups = ImportedQuotationAnalysis.Calculate(proposals, State(proposals) with { Links = [] }).ToArray();
        Assert.Equal(2, groups.Length); Assert.All(groups, group => { Assert.False(group.IsLinked); Assert.Null(group.Winner); Assert.Null(group.LowestUnitPrice); });
    }
    [Fact]
    public void UnknownDemandDifferentUnitsTiesAndInsufficientLotsStayPending()
    {
        SavedProposal[] proposals = [Proposal("A", 10, 2), Proposal("B", 10, 2)];
        Assert.True(Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, State(proposals, 5))).HasTie);
        Assert.Null(Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, State(proposals, 11))).Winner);
        Assert.Null(Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, State(proposals, null))).Winner);
        AnalyzedGroup conflict = Assert.Single(ImportedQuotationAnalysis.Calculate(proposals, State(proposals, 5) with { Links = [Link(proposals[0]), Link(proposals[1], 1, "KG")] }));
        Assert.True(conflict.HasUnitConflict); Assert.Null(conflict.LowestUnitPrice); Assert.Null(conflict.Winner);
    }
    [Fact]
    public async Task AnalysisRevisions_PersistOfflineAndProtectFinalizedAndOutdatedInputs()
    {
        SqliteImportStore store = new(_directory); await store.InitializeAsync(Token);
        Quotation quote = await store.SaveQuotationAsync(new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 0), Token);
        SavedProposal first = Proposal("A", 2, 200m, "BOB") with { QuotationId = quote.Id };
        SavedProposal second = Proposal("B", 200, 2.1m) with { QuotationId = quote.Id };
        await store.SaveProposalAsync(first, [1], Token); await store.SaveProposalAsync(second, [2], Token);
        AnalysisInput input = await store.GetAnalysisInputAsync(quote.Id, Token);
        AnalysisState linked = await store.SaveAnalysisAsync(input.State with { Links = [Link(first, 100), Link(second)] }, Token);
        AnalysisState demanded = await store.SaveAnalysisAsync(linked with { Demands = [new("manual:wire", "MT", 200)] }, Token);
        Assert.Equal(2, demanded.Version);
        AnalysisInput loaded = await new SqliteImportStore(_directory).GetAnalysisInputAsync(quote.Id, Token);
        Assert.Equal(2, loaded.State.Version); Assert.Equal(100, loaded.State.Links[0].BaseUnitsPerQuotedUnit);
        Assert.Equal(400m, Assert.Single(ImportedQuotationAnalysis.Calculate(loaded.Proposals, loaded.State)).WinnerCost);
        Assert.Equal(AnalysisError.Conflict, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(linked, Token))).Code);
        await store.SaveQuotationAsync(loaded.Quotation with { State = QuotationState.Finalized }, Token);
        Assert.Equal(AnalysisError.Conflict, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(demanded, Token))).Code);
        AnalysisInput finalized = await store.GetAnalysisInputAsync(quote.Id, Token);
        Assert.Equal(AnalysisError.ReadOnly, (await Assert.ThrowsAsync<AnalysisException>(() => store.SaveAnalysisAsync(finalized.State with { InputsRevision = finalized.Quotation.Revision }, Token))).Code);
    }
    private static DataTable ProductTable(Type? financialType = null)
    {
        DataTable table = new();
        foreach (string name in new[] { "Codigo", "Nome", "Semelhante", "FornecedorID", "Fornecedor", "CodigoFornecedor", "UnForcli", "UnidadeBase" }) table.Columns.Add(name, typeof(string));
        table.Columns.Add("Inativo", typeof(int)); table.Columns.Add("Quantidade", typeof(decimal)); table.Columns.Add("Data", typeof(DateTime)); table.Columns.Add("VlrUnit", financialType ?? typeof(decimal));
        return table;
    }
    private static void Add(DataTable table, string code, string alternative, decimal factor, string unit, object? price = null)
    {
        DataRow row = table.NewRow(); row["Codigo"] = code; row["Nome"] = "Synthetic product"; row["Semelhante"] = "Synthetic wire";
        row["FornecedorID"] = "Supplier-1"; row["Fornecedor"] = "Synthetic supplier"; row["CodigoFornecedor"] = alternative;
        row["UnForcli"] = unit; row["UnidadeBase"] = "MT"; row["Inativo"] = 0; row["Quantidade"] = factor;
        row["Data"] = price is null ? DBNull.Value : new DateTime(2026, 1, 1); row["VlrUnit"] = price ?? DBNull.Value; table.Rows.Add(row);
    }
    [Fact]
    public async Task ErpRows_PreserveAlternateCodesFactorsLeadingZerosAndBaseUnitPurchasePrice()
    {
        using DataTable table = ProductTable(); Add(table, "0123", "000.01", 1000, "KM", 1.1234567890m); Add(table, "0123", "BOB-CODE", 100, "BOB", 1.1234567890m); Add(table, "0456", "Other", 1, "MT");
        using var reader = table.CreateDataReader();
        var products = await ErpProductReader.ReadAsync(reader, "synthetic-catalog", DateTime.UtcNow, Token);
        Assert.Equal(3, products.Count); Assert.Equal("0123", products[0].ProductCode); Assert.Equal("000.01", products[0].SupplierProductCode);
        Assert.Equal(1000m, products[0].AlternativeQuantity); Assert.Equal(100m, products[1].AlternativeQuantity);
        Assert.Equal(1.1234567890m, products[1].LastPurchaseUnitPrice); Assert.Equal("MT", products[1].LastPurchaseUnit);
        Assert.Null(products[2].LastPurchaseDate); Assert.Null(products[2].LastPurchaseUnitPrice);
    }
    [Fact]
    public async Task LastPurchase_AlwaysUsesProductBaseUnit_EvenWhenAnotherUnitIsReturned()
    {
        using DataTable table = ProductTable(); table.Columns.Add("UnidadeUltimaCompra", typeof(string));
        Add(table, "0123", "BOB-CODE", 100, "BOB", 1.8m); table.Rows[0]["UnidadeUltimaCompra"] = "RL";
        using var reader = table.CreateDataReader();
        var products = await ErpProductReader.ReadAsync(reader, "synthetic-catalog", DateTime.UtcNow, Token);
        Assert.Equal("MT", products[0].LastPurchaseUnit); Assert.Equal(1.8m, products[0].LastPurchaseUnitPrice);
    }

    [Fact]
    public async Task ErpRows_RejectInvalidSchemaAndBinaryFloatingPointMoney()
    {
        using DataTable missing = new(); missing.Columns.Add("Codigo"); using var first = missing.CreateDataReader();
        Assert.Equal(ErpError.InvalidResult, (await Assert.ThrowsAsync<ErpException>(() => ErpProductReader.ReadAsync(first, "catalog", DateTime.UtcNow, Token))).Code);
        using DataTable wrong = ProductTable(typeof(double)); Add(wrong, "1", "2", 1, "MT", 1.5d); using var second = wrong.CreateDataReader();
        Assert.Equal(ErpError.InvalidResult, (await Assert.ThrowsAsync<ErpException>(() => ErpProductReader.ReadAsync(second, "catalog", DateTime.UtcNow, Token))).Code);
    }
    [Theory]
    [InlineData("SELECT @SimilarityCode AS Value")]
    [InlineData("SELECT @SimilarityCode AS Value OUTER APPLY (SELECT TOP(1) Value FROM ExampleTable) lastRow;")]
    [InlineData("-- SELECT fake\nSELECT @SimilarityCode, 'DELETE; INTO' AS [Update]")]
    public void ProductQuery_AllowsOneParameterizedRead(string query) => ProductReadQuery.Validate(query);
    [Theory]
    [InlineData("SELECT 'literal @SimilarityCode'")]
    [InlineData("SELECT @SimilarityCode INTO Archive")]
    [InlineData("SELECT @SimilarityCode; DELETE FROM ExampleTable")]
    [InlineData("SELECT @SimilarityCode DELETE FROM ExampleTable")]
    [InlineData("EXEC Example @SimilarityCode")]
    [InlineData("SELECT @SimilarityCode SELECT Secret FROM ExampleTable")]
    public void ProductQuery_RejectsMissingParametersAndWriteOrMultipleStatements(string query) =>
        Assert.Equal(ErpError.InvalidQuery, Assert.Throws<ErpException>(() => ProductReadQuery.Validate(query)).Code);
    [Fact]
    public async Task SavedCredentials_AreProtectedScopedAndNeverReturnedThroughConfiguration()
    {
        Directory.CreateDirectory(_directory); ProtectedErpCredentials store = new(_directory);
        ErpConfiguration config = new() { Server = "synthetic-server", Database = "example", UserName = "reader", Authentication = ErpAuthentication.SqlLogin };
        const string password = "synthetic-secret-not-a-real-credential";
        bool nativeAvailable = true;
        if (OperatingSystem.IsWindows())
        {
            try { _ = System.Security.Cryptography.ProtectedData.Protect([1], null, System.Security.Cryptography.DataProtectionScope.CurrentUser); }
            catch (System.Security.Cryptography.CryptographicException) { nativeAvailable = false; }
        }
        if (!nativeAvailable)
        {
            Assert.Equal(ErpError.CredentialsUnavailable, (await Assert.ThrowsAsync<ErpException>(() => store.SaveAsync(config, password, Token))).Code);
            Assert.False(Directory.Exists(Path.Combine(_directory, "credentials"))); return;
        }
        await store.SaveAsync(config, password, Token); Assert.Equal(password, await store.GetAsync(config, Token));
        Assert.Null(await store.GetAsync(config with { UserName = "other" }, Token));
        Assert.Equal(ProtectedErpCredentials.CatalogIdentity(config), ProtectedErpCredentials.CatalogIdentity(config with { UserName = "other" }));
        if (store.CanPersist)
        {
            string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "credentials"), "*.bin")); byte[] encrypted = await File.ReadAllBytesAsync(path, Token);
            Assert.True(encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) < 0);
            Assert.Equal(password, await new ProtectedErpCredentials(_directory).GetAsync(config, Token));
            encrypted[^1] ^= 1; await File.WriteAllBytesAsync(path, encrypted, Token);
            Assert.Equal(ErpError.CredentialsUnavailable, (await Assert.ThrowsAsync<ErpException>(() => store.GetAsync(config, Token))).Code);
        }
    }
    [Fact]
    public async Task CredentialProtectionFailure_IsSanitizedWithoutWritingPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        ProtectedErpCredentials store = new(_directory, new UnavailableProtection());
        ErpConfiguration config = new() { Server = "synthetic-server", Database = "example", UserName = "reader" };
        ErpException error = await Assert.ThrowsAsync<ErpException>(() => store.SaveAsync(config, "synthetic-secret", Token));
        Assert.Equal(ErpError.CredentialsUnavailable, error.Code); Assert.False(Directory.Exists(Path.Combine(_directory, "credentials")));
    }
    private sealed class UnavailableProtection : IUserSecretProtection
    {
        public byte[] Protect(byte[] clear) => throw new System.Security.Cryptography.CryptographicException("Synthetic provider unavailable.");
        public byte[] Unprotect(byte[] encrypted) => throw new System.Security.Cryptography.CryptographicException("Synthetic provider unavailable.");
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

