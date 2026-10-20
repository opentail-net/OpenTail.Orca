using System.Text;

namespace OpenTail.Modules.Sql;

public enum SqlKind { Select, Insert, Update, Delete }

/// <summary>
/// The database's SQL dialect, for the quoting and comment rules <see cref="SqlGuard"/> must know. <see cref="Unknown"/> (a custom
/// provider factory) applies every dialect's refusals.
/// </summary>
public enum SqlDialect { Unknown, Sqlite, SqlServer, Postgres, MySql }

/// <summary>
/// Best-effort statement classification. This is a seatbelt, not the security boundary: the boundary is a
/// least-privilege database login (or a read-only connection). The guard exists so obvious mistakes and
/// prompt-injected statements are refused before they reach the database or a reviewer.
/// </summary>
/// <remarks>
/// The guard does not try to be a full lexer for each dialect. Quoting or comments it cannot model safely are refused instead
/// (fail closed): Postgres dollar quotes and <c>E'...'</c> strings, backslashes inside literals on Postgres and MySQL, MySQL
/// <c>#</c> comments, <c>/*!</c> executable comments and <c>--</c> without a following space, nested block comments, and
/// unterminated literals or comments. The model can rewrite a query without them, using parameters for values.
/// </remarks>
public static class SqlGuard
{
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC", "EXECUTE", "MERGE", "GRANT", "REVOKE", "DENY", "ATTACH", "DETACH",
        "PRAGMA", "VACUUM", "REINDEX", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "BULK", "LOAD_EXTENSION", "SHUTDOWN",
        "KILL", "BACKUP", "RESTORE", "DBCC", "COPY", "CALL", "DO",
    };

    private static readonly HashSet<string> AlsoNeverInSelect = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "INTO",
    };

    /// <summary>Classifies with every dialect's refusals (<see cref="SqlDialect.Unknown"/>).</summary>
    public static SqlKind Classify(string sql) => Classify(sql, SqlDialect.Unknown);

    /// <summary>Returns the cleaned statement kind, or throws <see cref="ArgumentException"/> explaining why it is refused.</summary>
    public static SqlKind Classify(string sql, SqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("An SQL statement is required.");
        if (sql.Length > 8000) throw new ArgumentException("The statement is longer than 8000 characters.");

        var tokens = Tokenise(sql, dialect, out var semicolons);
        if (tokens.Count == 0) throw new ArgumentException("The statement is empty.");
        if (semicolons > 0) throw new ArgumentException("Only one statement is allowed, without a trailing semicolon chain.");

        foreach (var t in tokens)
        {
            if (Never.Contains(t)) throw new ArgumentException($"'{t.ToUpperInvariant()}' is not allowed.");
            if (t.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) || t.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("System procedures are not allowed.");
        }

        var first = tokens[0].ToUpperInvariant();
        var kind = first switch
        {
            "SELECT" or "WITH" => SqlKind.Select,
            "INSERT" => SqlKind.Insert,
            "UPDATE" => SqlKind.Update,
            "DELETE" => SqlKind.Delete,
            _ => throw new ArgumentException($"Statements must start with SELECT, WITH, INSERT, UPDATE or DELETE, not '{first}'."),
        };

        if (kind == SqlKind.Select)
        {
            var bad = tokens.FirstOrDefault(t => AlsoNeverInSelect.Contains(t));
            if (bad is not null) throw new ArgumentException($"'{bad.ToUpperInvariant()}' is not allowed in a query.");
        }
        else if (kind is SqlKind.Update or SqlKind.Delete && !tokens.Any(t => t.Equals("WHERE", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{kind.ToString().ToUpperInvariant()} needs a WHERE clause.");

        return kind;
    }

    internal static List<string> Tokenise(string sql, out int extraSemicolons) => Tokenise(sql, SqlDialect.Unknown, out extraSemicolons);

    /// <summary>
    /// Words outside string literals, quoted identifiers and comments. Counts statement-separating semicolons. Throws
    /// <see cref="ArgumentException"/> for quoting or comments the dialect has and this method does not model.
    /// </summary>
    internal static List<string> Tokenise(string sql, SqlDialect dialect, out int extraSemicolons)
    {
        var postgres = dialect is SqlDialect.Postgres or SqlDialect.Unknown;
        var mysql = dialect is SqlDialect.MySql or SqlDialect.Unknown;
        var backslashEscapes = postgres || mysql;

        var tokens = new List<string>();
        var word = new StringBuilder();
        extraSemicolons = 0;
        var trailing = sql.TrimEnd();
        if (trailing.EndsWith(';')) sql = trailing[..^1];

        void Flush() { if (word.Length > 0) { tokens.Add(word.ToString()); word.Clear(); } }

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'')
            {
                if (postgres && word.Length == 1 && word[0] is 'E' or 'e')
                    throw new ArgumentException("E'...' escape strings are not allowed; pass the value as a parameter.");
                Flush();
                i = Skip(sql, i, '\'', backslashEscapes);
                continue;
            }
            if (c == '"') { Flush(); i = Skip(sql, i, '"', mysql); continue; } // a string in MySQL, an identifier elsewhere
            if (c == '[') { Flush(); i = Skip(sql, i, ']', false); continue; }
            if (c == '`') { Flush(); i = Skip(sql, i, '`', false); continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                // MySQL starts a comment only at "-- " followed by whitespace; "--x" is two minus signs and the rest runs.
                if (mysql && i + 2 < sql.Length && !char.IsWhiteSpace(sql[i + 2]))
                    throw new ArgumentException("'--' must be followed by a space to start a comment.");
                Flush();
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '#' && mysql) throw new ArgumentException("'#' comments are not allowed; use '-- '.");
            if (c == '$' && postgres) throw new ArgumentException("'$' is not allowed (dollar quotes or $n parameters); use @name parameters.");
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                if (mysql && i + 2 < sql.Length && (sql[i + 2] == '!' || (sql[i + 2] == 'M' && i + 3 < sql.Length && sql[i + 3] == '!')))
                    throw new ArgumentException("MySQL executable comments (/*! ... */, /*M! ... */) are not allowed.");
                Flush();
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("The statement has an unterminated /* comment.");
                // Postgres and SQL Server nest block comments; SQLite and MySQL do not. Refuse nesting rather than guess.
                if (sql.IndexOf("/*", i + 2, end - (i + 2), StringComparison.Ordinal) >= 0)
                    throw new ArgumentException("Nested /* comments are not allowed.");
                i = end + 1;
                continue;
            }
            if (c == ';') { Flush(); extraSemicolons++; continue; }
            if (char.IsLetterOrDigit(c) || c == '_') word.Append(c);
            else Flush();
        }
        Flush();
        return tokens;
    }

    /// <summary>
    /// The index of the closing quote. A doubled quote inside is an escaped one, in every dialect. Where the dialect may treat a
    /// backslash as an escape, a backslash is refused rather than modelled.
    /// </summary>
    private static int Skip(string s, int start, char close, bool refuseBackslash)
    {
        for (var i = start + 1; i < s.Length; i++)
        {
            if (refuseBackslash && s[i] == '\\')
                throw new ArgumentException("Backslashes inside quoted text are not allowed for this database; pass the value as a parameter.");
            if (s[i] != close) continue;
            if (i + 1 < s.Length && s[i + 1] == close) { i++; continue; } // '' "" ]] `` inside
            return i;
        }
        throw new ArgumentException($"The statement has unterminated quoted text (missing {close}).");
    }
}
