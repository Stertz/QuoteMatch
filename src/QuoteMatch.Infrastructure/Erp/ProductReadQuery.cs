using System.Text;
using QuoteMatch.Domain.Integration;
namespace QuoteMatch.Infrastructure.Erp;

// This limits configured query shapes; database permissions remain the read-only boundary.
public static class ProductReadQuery
{
    public static void Validate(string sql, string requiredParameter = "@SimilarityCode", ProductReadMode mode = ProductReadMode.SingleQuery)
    {
        if (!Enum.IsDefined(mode)) throw new ErpException(ErpError.InvalidQuery);
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 32000) throw new ErpException(ErpError.InvalidQuery);
        string input = sql.Trim(); if (input.EndsWith(';')) input = input[..^1];
        HashSet<string> forbidden = new("INSERT UPDATE DELETE MERGE EXEC EXECUTE DROP ALTER CREATE TRUNCATE GRANT REVOKE DENY WAITFOR DBCC USE SET BACKUP RESTORE INTO OPENROWSET OPENQUERY OPENDATASOURCE BULK GO".Split(' '), StringComparer.OrdinalIgnoreCase);
        List<string> tokens = []; int depth = 0, selects = 0;
        for (int index = 0; index < input.Length;)
        {
            char value = input[index];
            if (char.IsWhiteSpace(value)) { index++; continue; }
            if (value == '-' && index + 1 < input.Length && input[index + 1] == '-') { while (index < input.Length && input[index] != '\n') index++; continue; }
            if (value == '/' && index + 1 < input.Length && input[index + 1] == '*')
            {
                int comments = 1; index += 2;
                while (index < input.Length && comments > 0)
                {
                    if (index + 1 < input.Length && input[index] == '/' && input[index + 1] == '*') { comments++; index += 2; }
                    else if (index + 1 < input.Length && input[index] == '*' && input[index + 1] == '/') { comments--; index += 2; }
                    else index++;
                }
                if (comments != 0) throw new ErpException(ErpError.InvalidQuery); continue;
            }
            if (value is '\'' or '[' or '"')
            {
                char end = value == '[' ? ']' : value; index++; bool closed = false;
                while (index < input.Length)
                {
                    if (input[index++] != end) continue;
                    if (index < input.Length && input[index] == end) { index++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new ErpException(ErpError.InvalidQuery); continue;
            }
            if (value == ';' && mode == ProductReadMode.SingleQuery) throw new ErpException(ErpError.InvalidQuery);
            if (value == '(') { depth++; index++; continue; }
            if (value == ')') { if (--depth < 0) throw new ErpException(ErpError.InvalidQuery); index++; continue; }
            if (char.IsLetter(value) || value is '@' or '_')
            {
                int start = index++; while (index < input.Length && (char.IsLetterOrDigit(input[index]) || input[index] is '@' or '_' or '#')) index++;
                string token = input[start..index]; tokens.Add(token);
                if (mode == ProductReadMode.SingleQuery && forbidden.Contains(token)) throw new ErpException(ErpError.InvalidQuery);
                if (depth == 0 && token.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) selects++;
                continue;
            }
            index++;
        }
        if (depth != 0 || mode == ProductReadMode.SingleQuery && (selects != 1 || tokens.Count == 0 || !(tokens[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            || !tokens.Contains(requiredParameter, StringComparer.OrdinalIgnoreCase)) throw new ErpException(ErpError.InvalidQuery);
    }
}
