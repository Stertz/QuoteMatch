using QuoteMatch.Application.Integration;
using QuoteMatch.Domain.Analysis;
using QuoteMatch.Domain.Importing;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Domain.Quotations;
using QuoteMatch.Infrastructure.Erp;

namespace QuoteMatch.Tests;

public sealed class ErpIdentificationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ErpProductCandidate Product(string alternative, string product = "00123", string group = "WIRE") =>
        new(product, "Synthetic product", group, false, "1", "Synthetic supplier", alternative, 100, "BOB", null, 1.8m, "MT", "MT", "synthetic-catalog", DateTime.UtcNow);

    private static AnalysisInput Input(params ReviewedItem[] items)
    {
        Quotation quote = new(Guid.NewGuid(), "Synthetic study", "", DateTime.UtcNow, DateTime.UtcNow, QuotationState.Draft, 1);
        SavedProposal proposal = new(Guid.NewGuid(), DateTime.UtcNow, "synthetic.pdf", "hash", new PdfTemplate { SupplierName = "Synthetic supplier" }, new([], "Synthetic"), new([], [], null, null), items) { QuotationId = quote.Id };
        return new(quote, [proposal], new(quote.Id, 0, 1, DateTime.UtcNow, [], []));
    }
    private static ReviewedItem Item(string code, bool excluded = false) => new(Guid.NewGuid(), code, "Synthetic wire", 2, 180, 360, "BOB", excluded, excluded ? "Synthetic exclusion" : "");

    [Fact]
    public async Task Batch_QueriesDistinctPdfCodes_SkippingExcludedAndAlreadyLinkedItems()
    {
        AnalysisInput input = Input(Item("000.01"), Item("000.01"), Item("SECOND"), Item("EXCLUDED", true), Item("LINKED"));
        var linkedItem = input.Proposals[0].Review[^1];
        input = input with { State = input.State with { Links = [new(input.Proposals[0].Id, linkedItem.CaptureId, "manual:existing", "Existing", "MT", 100, null, DateTime.UtcNow)] } };
        Lookup lookup = new(code => [Product(code)]);
        var result = await new ErpIdentificationService(lookup).LookupPendingAsync(input, Token);
        Assert.Equal(new[] { "000.01", "SECOND" }, lookup.Requests);
        Assert.Equal(2, result.Count); Assert.Single(result["000.01"]); Assert.Single(input.State.Links);
    }

    [Fact]
    public async Task Batch_PreservesLeadingZerosPunctuationAndAmbiguity_WithoutGuessing()
    {
        Lookup lookup = new(_ => [Product("000.01"), Product("000.01", "00456"), Product("00001"), Product("1")]);
        var result = await new ErpIdentificationService(lookup).LookupPendingAsync(Input(Item("000.01")), Token);
        Assert.Equal(2, result["000.01"].Count); Assert.Equal(new[] { "00123", "00456" }, result["000.01"].Select(product => product.ProductCode));
        Assert.False(ErpProductIdentity.MatchesSupplierCode(Product("00001"), "000.01"));
        Assert.False(ErpProductIdentity.MatchesSupplierCode(Product("1"), "000.01"));
        Assert.True(ErpProductIdentity.MatchesSupplierCode(Product("Ab.001"), "ab.001"));
    }

    [Fact]
    public async Task Batch_EmptyMatchesStayUnresolved_AndCancellationPreservesSavedInput()
    {
        AnalysisInput input = Input(Item("FIRST"), Item("SECOND"));
        Lookup lookup = new(_ => []);
        var result = await new ErpIdentificationService(lookup).LookupPendingAsync(input, Token);
        Assert.All(result.Values, Assert.Empty); Assert.Empty(input.State.Links);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        lookup.Requests.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ErpIdentificationService(lookup).LookupPendingAsync(input, canceled.Token));
        Assert.Empty(lookup.Requests); Assert.Empty(input.State.Links);
    }

    [Fact]
    public void Grouping_UsesErpSimilarityAcrossDifferentProductAndSupplierCodes()
    {
        AnalysisInput input = Input(Item("SUPPLIER-A"), Item("SUPPLIER-B"), Item("OTHER"));
        ErpProductCandidate[] products = [Product("SUPPLIER-A", "00123"), Product("SUPPLIER-B", "00456"), Product("OTHER", "00789", "OTHER GROUP")];
        var links = input.Proposals[0].Review.Select((item, index) => new ItemLink(input.Proposals[0].Id, item.CaptureId, ErpProductIdentity.GroupKey(products[index]),
            ErpProductIdentity.GroupName(products[index]), "MT", 100, products[index], DateTime.UtcNow)).ToArray();
        var groups = ImportedQuotationAnalysis.Calculate(input.Proposals, input.State with { Links = links });
        Assert.Equal(2, groups.Count); Assert.Equal(2, groups.Single(group => group.Name == "WIRE").Offers.Count);
        Assert.All(groups.SelectMany(group => group.Offers), offer => Assert.Equal(200m, offer.BaseQuantity));
        Assert.Equal("SUPPLIER-A", input.Proposals[0].Review[0].ProductCode);
        Assert.Equal("00123", links[0].Product!.ProductCode);
    }

    [Fact]
    public void MissingSimilarity_NeverGroupsUnrelatedProductsOrCatalogs()
    {
        Assert.NotEqual(ErpProductIdentity.GroupKey(Product("A", "001", "")), ErpProductIdentity.GroupKey(Product("B", "002", "")));
        Assert.NotEqual(ErpProductIdentity.GroupKey(Product("A")), ErpProductIdentity.GroupKey(Product("A") with { CatalogIdentity = "different-catalog" }));
        Assert.Equal("Synthetic product", ErpProductIdentity.GroupName(Product("A", group: "")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Gateway_RejectsMissingCodeBeforeLoadingConfiguration(string code)
    {
        Configuration config = new(new());
        ErpGateway gateway = new(config, new Credentials());
        Assert.Equal(ErpError.InvalidQuery, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync(code, Token))).Code);
        Assert.Equal(0, config.Reads);
    }

    [Fact]
    public async Task Gateway_ValidatesCodeOperationConfigurationAndCredentialsBeforeConnecting()
    {
        Credentials credentials = new(); Configuration config = new(new()); ErpGateway gateway = new(config, credentials);
        Assert.Equal(ErpError.NotConfigured, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync("000.01", Token))).Code);
        config.Value = new() { Server = "synthetic-server", Database = "synthetic-database", Authentication = ErpAuthentication.SqlLogin };
        Assert.Equal(ErpError.QueryMissing, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync("000.01", Token))).Code);
        config.Value = config.Value with { Queries = new() { [ErpOperation.FindProduct] = "SELECT @SimilarityCode" } };
        Assert.Equal(ErpError.InvalidQuery, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync("000.01", Token))).Code);
        config.Value = config.Value with { Queries = new() { [ErpOperation.FindProduct] = "SELECT @SupplierProductCode" } };
        Assert.Equal(ErpError.CredentialsUnavailable, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync("000.01", Token))).Code);
        Assert.Equal(ErpError.InvalidQuery, (await Assert.ThrowsAsync<ErpException>(() => gateway.FindByCodeAsync(new string('x', 201), Token))).Code);
    }

    [Fact]
    public async Task IntegratedConnectionTest_DoesNotReadSqlLoginCredentials()
    {
        ErpGateway gateway = new(new Configuration(new()), new UnavailableCredentials());
        Assert.False(await gateway.TestAsync(new() { Authentication = ErpAuthentication.Integrated }, null, Token));
    }

    private sealed class UnavailableCredentials : IErpCredentials
    {
        public bool CanPersist => false;
        public Task SaveAsync(ErpConfiguration configuration, string password, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<string?> GetAsync(ErpConfiguration configuration, CancellationToken cancellationToken) => throw new ErpException(ErpError.CredentialsUnavailable);
    }

    [Fact]
    public async Task ConnectionDiagnostics_KeepProfileValidationSeparateFromNetworkAndQueryExecution()
    {
        ErpGateway gateway = new(new Configuration(new()), new UnavailableCredentials());
        Assert.Equal(ErpError.NotConfigured, (await Assert.ThrowsAsync<ErpException>(() => gateway.TestSavedDetailedAsync(Token))).Code);
        var form = await gateway.TestDetailedAsync(new(), null, Token);
        Assert.False(form.Connected); Assert.Equal(ErpFailureStage.Configuration, form.Failure!.Stage);
        Assert.Null(form.Failure.SqlErrorNumber); Assert.Null(form.Failure.NativeErrorCode);
        var failure = new ErpException(ErpError.QueryFailed, new(ErpFailureStage.Query, 229));
        string json = System.Text.Json.JsonSerializer.Serialize(new { Status = failure.Code.ToString(), failure.Failure });
        Assert.Contains("Query", json); Assert.Contains("229", json);
        Assert.DoesNotContain("Password", json); Assert.DoesNotContain("ConnectionString", json);
    }

    [Theory]
    [InlineData(ErpAuthentication.Integrated, false)]
    [InlineData(ErpAuthentication.SqlLogin, true)]
    public void SharedConnectionOptions_UseReadIntentEncryptionAndTheExplicitAuthentication(ErpAuthentication authentication, bool trust)
    {
        var options = ErpConnectionOptions.Create(new()
        {
            Server = "synthetic-host",
            Database = "synthetic-catalog",
            Authentication = authentication,
            UserName = "reader",
            TrustServerCertificate = trust
        }, "synthetic-password");
        Assert.Equal("synthetic-host", options.DataSource); Assert.Equal("synthetic-catalog", options.InitialCatalog);
        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, options.ApplicationIntent);
        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, options.Encrypt);
        Assert.Equal(trust, options.TrustServerCertificate); Assert.Equal(30, options.ConnectTimeout);
        Assert.Equal(authentication == ErpAuthentication.Integrated, options.IntegratedSecurity);
        Assert.Equal(authentication == ErpAuthentication.Integrated ? "" : "reader", options.UserID);
        Assert.Equal(authentication == ErpAuthentication.Integrated ? "" : "synthetic-password", options.Password);
    }

    [Fact]
    public void CodeQuery_RequiresTheCodeParameterOutsideCommentsAndLiterals()
    {
        ProductReadQuery.Validate("SELECT @SupplierProductCode", "@SupplierProductCode");
        Assert.Equal(ErpError.InvalidQuery, Assert.Throws<ErpException>(() => ProductReadQuery.Validate("SELECT '@SupplierProductCode' -- @SupplierProductCode", "@SupplierProductCode")).Code);
        Assert.Equal(ErpError.InvalidQuery, Assert.Throws<ErpException>(() => ProductReadQuery.Validate("SELECT @SupplierProductCode; UPDATE Example SET Value=1", "@SupplierProductCode")).Code);
    }

    private sealed class Lookup(Func<string, IReadOnlyList<ErpProductCandidate>> resolve) : IErpProductLookup
    {
        public List<string> Requests { get; } = [];
        public Task<IReadOnlyList<ErpProductCandidate>> FindByCodeAsync(string supplierProductCode, CancellationToken cancellationToken)
        { Requests.Add(supplierProductCode); return Task.FromResult(resolve(supplierProductCode)); }
        public Task<IReadOnlyList<ErpProductCandidate>> FindBySimilarityAsync(string similarityCode, CancellationToken cancellationToken) => throw new InvalidOperationException("Batch identification must use PDF codes.");
    }
    [Theory]
    [InlineData(false, false, 45)]
    [InlineData(false, true, 30)]
    [InlineData(true, false, 60)]
    [InlineData(true, true, 0)]
    public void SharedConnectionOptions_RespectPersistedTransportSettings(bool encrypt, bool trust, int timeout)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { Encrypt = encrypt, TrustServerCertificate = trust, ConnectionTimeoutSeconds = timeout });
        ErpConfiguration configuration = System.Text.Json.JsonSerializer.Deserialize<ErpConfiguration>(json)!;
        var options = ErpConnectionOptions.Create(configuration, string.Empty);

        Assert.Equal(encrypt ? Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory : Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, options.Encrypt);
        Assert.Equal(trust, options.TrustServerCertificate);
        Assert.Equal(timeout > 0 ? timeout : 30, options.ConnectTimeout);
    }

    [Fact]
    public void SharedConnectionOptions_LegacyProfilesKeepEncryptionEnabled()
    {
        ErpConfiguration configuration = System.Text.Json.JsonSerializer.Deserialize<ErpConfiguration>("{}")!;
        var options = ErpConnectionOptions.Create(configuration, string.Empty);
        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, options.Encrypt);
        Assert.Equal(30, options.ConnectTimeout);
    }

    private sealed class Configuration(ErpConfiguration value) : IErpConfigurationRepository
    {
        public ErpConfiguration Value { get; set; } = value;
        public int Reads { get; private set; }
        public Task<ErpConfiguration> GetErpConfigurationAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(Value); }
    }
    private sealed class Credentials : IErpCredentials
    {
        public bool CanPersist => false;
        public Task SaveAsync(ErpConfiguration configuration, string password, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAsync(ErpConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
