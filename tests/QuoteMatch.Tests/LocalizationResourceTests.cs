using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QuoteMatch.Tests;

public sealed partial class LocalizationResourceTests
{
    [Fact]
    public void PortugueseResources_CoverEnglishKeysAndFormatArguments()
    {
        Dictionary<string, string> english = Load("Strings.resx");
        Dictionary<string, string> portuguese = Load("Strings.pt-BR.resx");

        Assert.Equal(english.Keys.Order(), portuguese.Keys.Order());
        foreach ((string key, string value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(portuguese[key]));
            Assert.Equal(Placeholders().Matches(value).Select(match => match.Value).Order(),
                Placeholders().Matches(portuguese[key]).Select(match => match.Value).Order());
        }
    }

    [Fact]
    public void ViewResourceBindings_ReferenceExistingTranslationKeys()
    {
        Dictionary<string, string> resources = Load("Strings.resx");
        string view = string.Join("\\n", Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Views"), "*.axaml").Select(File.ReadAllText));

        foreach (Match match in ResourceBindings().Matches(view))
        {
            Assert.True(resources.ContainsKey(match.Groups[1].Value), $"Missing resource: {match.Groups[1].Value}");
        }
    }

    private static Dictionary<string, string> Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", fileName))
            .Root!.Elements("data")
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.pt-BR.resx")]
    public void ProductQueryHelp_PreservesTheBaseUnitAliasWithoutPrescribingARemoteSchema(string fileName)
    {
        Dictionary<string, string> resources = Load(fileName);
        foreach (string key in new[] { "QueryContractFindProduct", "QueryContractGetEquivalentProducts", "QueryContractFindProductById" })
        {
            Assert.Contains("UnidadeBase", resources[key], StringComparison.Ordinal);
            Assert.DoesNotContain("p.Unidade", resources[key], StringComparison.Ordinal);
            Assert.DoesNotContain("CodigoAlternativo.", resources[key], StringComparison.Ordinal);
            Assert.DoesNotContain("Produto.", resources[key], StringComparison.Ordinal);
            Assert.DoesNotContain("p.Un AS", resources[key], StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"\{\d+(?::[^}]+)?\}")]
    private static partial Regex Placeholders();

    [GeneratedRegex(@"Strings\[([A-Za-z]+)\]")]
    private static partial Regex ResourceBindings();
}


