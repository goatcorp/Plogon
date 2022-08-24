using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#pragma warning disable CS1591

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

    public override string ToString() {
        if (rows.Count == 1)
        {
            return rows.First().Aggregate((current, col) => current + $"{col} - ")[..^3] + "\n";
        }
        
        var output = "|";
        foreach (var col in cols) output += $"{col}|";
        output += "\n|";
        
        foreach (var col in cols) output += $"{new string('-', col.Length)}|";
        output += "\n";

        foreach (var row in rows) {
            output += "|";
            foreach (var col in row) output += $"{col}|";
            output += "\n";
        }

        return output;
    }
}