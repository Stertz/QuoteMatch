using Microsoft.Data.Sqlite;
using QuoteMatch.Domain.Integration;
using QuoteMatch.Infrastructure.Erp;

namespace QuoteMatch.Tests;

public sealed class ProductNameSearchTests
{
    [Theory]
    [InlineData(" cabo  flex\t2,5 azul ", "CABO FLEXIVEL 02,50MM AZUL", true)]
    [InlineData("cabo flex 2,5 azul", "CABO FLEXIVEL 01,50MM AZUL", false)]
    [InlineData("cabo flex azul", "CABO AZUL FLEXIVEL", false)]
    [InlineData("cabo % _ [ \\", "CABO 100% _ [ \\", true)]
    [InlineData("cabo % _ [ \\", "CABO 100X Y [ \\", false)]
    public async Task NameTerms_MatchAcrossWordsAndPreserveLiteralWildcards(string query, string name, bool expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT $name LIKE '%' || $pattern || '%' ESCAPE '\\'";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$pattern", ProductNameSearch.BuildPattern(query));
        Assert.Equal(expected ? 1L : 0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    public void NameTerms_RejectInsufficientInput(string query) => Assert.Throws<ErpException>(() => ProductNameSearch.BuildPattern(query));
}
