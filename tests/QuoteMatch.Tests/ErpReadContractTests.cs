using System.Data;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Erp;

namespace QuoteMatch.Tests;

public sealed class ErpReadContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DataTable Products()
    {
        DataTable table = new();
        foreach (string name in new[] { "Codigo", "Nome", "Semelhante", "FornecedorID", "Fornecedor", "CodigoFornecedor", "UnForcli", "UnidadeBase", "BrandId", "BrandName" }) table.Columns.Add(name, typeof(string));
        table.Columns.Add("Inativo", typeof(bool)); table.Columns.Add("Quantidade", typeof(decimal));
        table.Columns.Add("Data", typeof(DateTime)); table.Columns.Add("VlrUnit", typeof(decimal));
        var row = table.NewRow(); row["Codigo"] = "000.01"; row["Nome"] = "Synthetic wire"; row["Inativo"] = false;
        row["FornecedorID"] = "manufacturer-1"; row["Fornecedor"] = "Synthetic manufacturer";
        row["CodigoFornecedor"] = "001-01"; row["UnidadeBase"] = "M"; row["Quantidade"] = 100m;
        row["BrandId"] = "brand-1"; row["BrandName"] = "Synthetic brand"; row["VlrUnit"] = 1.23456789m;
        table.Rows.Add(row); return table;
    }

    [Fact]
    public void ProductScripts_RequireExplicitModeAndAnActualNamedParameter()
    {
        const string script = "SET NOCOUNT ON; DECLARE @Code nvarchar(200) = @SupplierProductCode; EXEC dbo.ReadProduct @Code;";
        Assert.Throws<ErpException>(() => ProductReadQuery.Validate(script, "@SupplierProductCode"));
        ProductReadQuery.Validate(script, "@SupplierProductCode", ProductReadMode.Script);
        ProductReadQuery.Validate("WITH p AS (SELECT @SupplierProductCode AS Code) SELECT * FROM p", "@SupplierProductCode");
        Assert.Throws<ErpException>(() => ProductReadQuery.Validate("EXEC dbo.ReadProduct '@SupplierProductCode' -- @SupplierProductCode", "@SupplierProductCode", ProductReadMode.Script));
        Assert.Throws<ErpException>(() => ProductReadQuery.Validate("EXEC dbo.ReadProduct @SupplierProductCode /*", "@SupplierProductCode", ProductReadMode.Script));
        Assert.Throws<ErpException>(() => ProductReadQuery.Validate(new string('x', 32001), "@SupplierProductCode", ProductReadMode.Script));
    }

    [Fact]
    public async Task Products_PreserveOptionalBrandMetadataAndRejectPartialOrAdditionalResults()
    {
        var table = Products();
        using (var reader = table.CreateDataReader())
        {
            var product = Assert.Single(await ErpProductReader.ReadAsync(reader, "catalog", DateTime.UnixEpoch, Token));
            Assert.Equal(new("catalog", "brand-1", "Synthetic brand"), product.Brand);
            Assert.Equal("manufacturer-1", product.SupplierId); Assert.Equal(1.23456789m, product.LastPurchaseUnitPrice);
        }
        table.Rows[0]["BrandName"] = DBNull.Value;
        using (var reader = table.CreateDataReader()) await Assert.ThrowsAsync<ErpException>(() => ErpProductReader.ReadAsync(reader, "catalog", DateTime.UnixEpoch, Token));
        table.Columns.Remove("BrandId"); table.Columns.Remove("BrandName");
        using (var reader = table.CreateDataReader()) Assert.Null(Assert.Single(await ErpProductReader.ReadAsync(reader, "catalog", DateTime.UnixEpoch, Token)).Brand);
        using (var reader = new DataTableReader([Products(), Products()]))
            await Assert.ThrowsAsync<ErpException>(() => ErpProductReader.ReadAsync(reader, "catalog", DateTime.UnixEpoch, Token));
    }

    [Fact]
    public void BuyingSupplier_RequiresMatchingBrandBranchAndHistoricalProvenance()
    {
        ErpBrandIdentity brand = new("catalog", "manufacturer-1", "Synthetic brand");
        BuyingSupplierSnapshot snapshot = new(brand, new("catalog", "distributor-9", "Synthetic distributor"), "order-42", "revision-1", "branch-1", new(2026, 10, 26), DateTime.UnixEpoch);
        Assert.Equal("distributor-9", BuyingSupplierRules.Validate(snapshot, brand, "branch-1").Supplier.Id);
        foreach (var invalid in new[] { snapshot with { Branch = "branch-2" }, snapshot with { Brand = brand with { Id = "other" } },
            snapshot with { Supplier = snapshot.Supplier with { CatalogIdentity = "other" } }, snapshot with { Supplier = null! }, snapshot with { SourceOrderId = "" }, snapshot with { SourceOrderRevision = "" } })
            Assert.Equal(ErpError.InvalidResult, Assert.Throws<ErpException>(() => BuyingSupplierRules.Validate(invalid, brand, "branch-1")).Code);
    }
}
