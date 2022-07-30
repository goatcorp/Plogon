using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Plogon;

public class MarkdownTableBuilder
{
    private readonly string[] cols;
    private readonly List<string[]> rows = new();

    private MarkdownTableBuilder(string[] cols)
    {
        this.cols = cols;
    }

    public static MarkdownTableBuilder Create(params string[] cols) => new MarkdownTableBuilder(cols);

    public MarkdownTableBuilder AddRow(params string[] values)
    {
        Debug.Assert(values.Length == cols.Length);
        this.rows.Add(values);

        return this;
    }

    public override string ToString()
    {
        var output = "|" + cols.Aggregate(string.Empty, (current, col) => current + $" {col} | ");
        output += "\n";
        output = "|" + cols.Aggregate(output, (current, col) => current + $" {new string('-', col.Length)} | ");
        output += "\n";

        foreach (var row in rows)
        {
            output = "|" + row.Aggregate(output, (current, col) => current + $" {col} | ");
            output += "\n";
        }

        return output;
    }
}