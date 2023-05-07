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

    public override string ToString() => GetText();

    public string GetText(bool noTable = false) {
        if (rows.Count == 1 || noTable)
        {
            var text = rows.Aggregate(string.Empty,
                (text, row) => text += row.Aggregate((rowtext, col) => rowtext + $"{col} - ")[..^3] + "\n");

            string[] wordsToDelete =
            {
                "<sup>",
                "</sup>",
                "<sub>",
                "</sub>",
            };

            foreach (var word in wordsToDelete)
            {
                text = text.Replace(word, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(text))
                return text[..^1];
            
            return string.Empty;
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