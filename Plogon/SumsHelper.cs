using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Plogon
{
    /// <summary>sha256sum Helper.</summary>
    public static class SumsHelper
    {
        /// <summary>Input regex (specific to sha256sum, untagged format, binary mode).</summary>
        // TODO: [GeneratedRegex(...)] not supported for properties in .net 8.0
        private static Regex SumRegex { get; } = new(@"^(?<hex>[0-9A-Fa-f]{64}) \*(?<filename>.+)$", RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant);

        /// <summary>Write hashes in sha256sum untagged format.</summary>
        /// <param name="writer"><see cref="TextWriter"/> to write into.</param>
        /// <param name="hashes">Hashes to write.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        public static async Task WriteAsync(TextWriter writer, IReadOnlyDictionary<string, byte[]> hashes, CancellationToken cancellationToken = default)
        {
            foreach (var (file, hash) in hashes)
            {
                //var hashHex = Convert.ToHexStringLower(hash); // TODO: Convert.ToHexStringLower(hash) is not available in .net 8.0
                var hashHex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                await writer.WriteLineAsync($"{hashHex} *{file}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Read hashes in sha256sum untagged format.</summary>
        /// <param name="reader"><see cref="TextReader"/> to read from.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>Hashes read from <paramref name="reader"/>.</returns>
        public static async Task<Dictionary<string, byte[]>> ReadAsync(TextReader reader, CancellationToken cancellationToken = default)
        {
            var hashes = new Dictionary<string, byte[]>();
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) return hashes;

                var match = SumRegex.Match(line);
                if (match.Success)
                {
                    var file = match.Groups["filename"].Value;
                    var hash = Convert.FromHexString(match.Groups["hex"].ValueSpan);
                    hashes.Add(file, hash);
                }
            }
        }
    }
}
