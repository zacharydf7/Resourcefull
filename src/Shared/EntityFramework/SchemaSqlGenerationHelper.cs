using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace Resourcefull.SqlServer;

/// <summary>
/// Qualifies Resourcefull's tables with <paramref name="schema"/> wherever the migrations name them without one, and
/// otherwise writes SQL as <paramref name="inner"/> does.
/// </summary>
internal sealed class SchemaSqlGenerationHelper(ISqlGenerationHelper inner, string schema) : ISqlGenerationHelper
{
    public string StatementTerminator => inner.StatementTerminator;

    public string BatchTerminator => inner.BatchTerminator;

    public string StartTransactionStatement => inner.StartTransactionStatement;

    public string CommitTransactionStatement => inner.CommitTransactionStatement;

    public string SingleLineCommentToken => inner.SingleLineCommentToken;

    public string DelimitIdentifier(string name, string? schema) => inner.DelimitIdentifier(name, schema ?? SchemaOf(name));

    public void DelimitIdentifier(StringBuilder builder, string name, string? schema) =>
        inner.DelimitIdentifier(builder, name, schema ?? SchemaOf(name));

    public string DelimitIdentifier(string identifier) => inner.DelimitIdentifier(identifier);

    public void DelimitIdentifier(StringBuilder builder, string identifier) => inner.DelimitIdentifier(builder, identifier);

    public string DelimitJsonPathElement(string pathElement) => inner.DelimitJsonPathElement(pathElement);

    public string GenerateParameterName(string name) => inner.GenerateParameterName(name);

    public void GenerateParameterName(StringBuilder builder, string name) => inner.GenerateParameterName(builder, name);

    public string GenerateParameterNamePlaceholder(string name) => inner.GenerateParameterNamePlaceholder(name);

    public void GenerateParameterNamePlaceholder(StringBuilder builder, string name) =>
        inner.GenerateParameterNamePlaceholder(builder, name);

    public string GenerateComment(string text) => inner.GenerateComment(text);

    public string GenerateCreateSavepointStatement(string name) => inner.GenerateCreateSavepointStatement(name);

    public string GenerateRollbackToSavepointStatement(string name) => inner.GenerateRollbackToSavepointStatement(name);

    public string GenerateReleaseSavepointStatement(string name) => inner.GenerateReleaseSavepointStatement(name);

    private string? SchemaOf(string name) => ResourceDbContext.TableNames.Contains(name) ? schema : null;
}
