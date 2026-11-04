using System.Data.Common;
using System.Globalization;
using QuoteMatch.Domain.Integration;
namespace QuoteMatch.Infrastructure.Erp;

public static class ErpProductReader
{
    private static readonly string[] Required = ["Codigo", "Nome", "Semelhante", "Inativo", "FornecedorID", "Fornecedor", "CodigoFornecedor", "Quantidade", "UnForcli", "Data", "VlrUnit"];
    public static async Task<IReadOnlyList<ErpProductCandidate>> ReadAsync(DbDataReader reader, string identity, DateTime fetchedAtUtc, CancellationToken cancellationToken)
    {
        while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken)) { }
        Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < reader.FieldCount; index++) if (!columns.TryAdd(reader.GetName(index), index)) throw new ErpException(ErpError.InvalidResult);
        if (Required.Any(name => !columns.ContainsKey(name))) throw new ErpException(ErpError.InvalidResult);
        List<ErpProductCandidate> result = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == 1000) throw new ErpException(ErpError.TooManyResults);
            object? Value(string name) => columns.TryGetValue(name, out int ordinal) && !reader.IsDBNull(ordinal) ? reader.GetValue(ordinal) : null;
            string Text(string name) => Convert.ToString(Value(name), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            decimal? Number(string name) => Value(name) switch
            {
                null => null,
                decimal value => value,
                byte value => value,
                short value => value,
                int value => value,
                long value => value,
                _ => throw new ErpException(ErpError.InvalidResult)
            };
            bool inactive = Value("Inativo") switch { false or 0 or (short)0 or (byte)0 => false, true or 1 or (short)1 or (byte)1 => true, _ => throw new ErpException(ErpError.InvalidResult) };
            DateOnly? date = Value("Data") switch { null => null, DateTime value => DateOnly.FromDateTime(value), DateOnly value => value, _ => throw new ErpException(ErpError.InvalidResult) };
            if (Text("Codigo").Length == 0 || Text("Nome").Length == 0) throw new ErpException(ErpError.InvalidResult);
            string brandId = Text("BrandId"), brandName = Text("BrandName");
            if ((brandId.Length == 0) != (brandName.Length == 0)) throw new ErpException(ErpError.InvalidResult);
            result.Add(new(Text("Codigo"), Text("Nome"), Text("Semelhante"), inactive, Text("FornecedorID"), Text("Fornecedor"),
                Text("CodigoFornecedor"), Number("Quantidade"), Text("UnForcli"), date, Number("VlrUnit"),
                Text("UnidadeBase") is { Length: > 0 } baseUnit ? baseUnit : null,
                Text("UnidadeBase") is { Length: > 0 } historyBase ? historyBase : null, identity, fetchedAtUtc)
            { Brand = brandId.Length == 0 ? null : new(identity, brandId, brandName) });
        }
        while (await reader.NextResultAsync(cancellationToken))
            if (reader.FieldCount != 0) throw new ErpException(ErpError.InvalidResult);
        return result;
    }
}

