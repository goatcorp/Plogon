using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Plogon
{
    /// <summary>Hashing utilities.</summary>
    public static class HashingUtils
    {
        /// <summary>Generate hashes for files found at <paramref name="archive"/>.</summary>
        /// <param name="archive"><see cref="ZipArchive"/> to generate hashes from.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing hashes of each of the files found.</returns>
        public static async Task<Dictionary<string, byte[]>> GenerateAsync(ZipArchive archive, CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, byte[]>(archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                await using var stream = entry.Open();
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                results.Add(entry.FullName, hash);
            }
            return results;
        }

        /// <summary>Generate hashes for files found at <paramref name="root"/>.</summary>
        /// <param name="root"><see cref="DirectoryInfo"/> to generate hashes from.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing hashes of each of the files found.</returns>
        public static async Task<Dictionary<string, byte[]>> GenerateAsync(DirectoryInfo root, CancellationToken cancellationToken = default)
        {
            var directories = new Queue<DirectoryInfo>([root]);
            var results = new Dictionary<string, byte[]>();
            while (directories.TryDequeue(out var directory))
            {
                directories.EnqueueRange(directory.EnumerateDirectories());
                foreach (var file in directory.EnumerateFiles())
                {
                    await using var stream = file.OpenRead();
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                    results.Add(Path.GetRelativePath(root.FullName, file.FullName), hash);
                }
            }
            return results;
        }

        /// <summary>Generate hashes for files found at <paramref name="path"/>.</summary>
        /// <param name="path">Directory or archive path to generate hashes from.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing hashes of each of the files found.</returns>
        public static async Task<Dictionary<string, byte[]>> GenerateAsync(string path, CancellationToken cancellationToken = default)
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var root = new DirectoryInfo(path);
                return await GenerateAsync(root, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var archive = ZipFile.OpenRead(path);
                return await GenerateAsync(archive, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Generate hashes for files found at <paramref name="archive"/>.</summary>
        /// <param name="archive"><see cref="FileInfo"/> representing a zip archive file to generate hashes from.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing hashes of each of the files found.</returns>
        public static async Task<Dictionary<string, byte[]>> GenerateAsync(FileInfo archive, CancellationToken cancellationToken = default)
        {
            using var zipArchive = ZipFile.OpenRead(archive.FullName);
            return await GenerateAsync(zipArchive, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Verify hashes for files at <paramref name="archive"/>.</summary>
        /// <param name="hashes">Hashes to verify.</param>
        /// <param name="archive"><see cref="ZipArchive"/> to verify hashes at.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>Verification result.</returns>
        public static async Task<bool> VerifyAsync(IReadOnlyDictionary<string, byte[]> hashes, ZipArchive archive, CancellationToken cancellationToken = default)
        {
            foreach (var (filePath, fileHash) in hashes)
            {
                var entry = archive.GetEntry(filePath);
                if (entry is null) return false;

                await using var stream = entry.Open();
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!fileHash.SequenceEqual(hash)) return false;
            }
            return true;
        }

        /// <summary>Verify hashes for files at <paramref name="root"/>.</summary>
        /// <param name="hashes">Hashes to verify.</param>
        /// <param name="root"><see cref="DirectoryInfo"/> to verify hashes at.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>Verification result.</returns>
        public static async Task<bool> VerifyAsync(IReadOnlyDictionary<string, byte[]> hashes, DirectoryInfo root, CancellationToken cancellationToken = default)
        {
            foreach (var (filePath, fileHash) in hashes)
            {
                var fullPath = Path.Join(root.FullName, filePath);
                if (!File.Exists(fullPath)) return false;

                await using var stream = File.OpenRead(fullPath);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!fileHash.SequenceEqual(hash)) return false;
            }
            return true;
        }

        /// <summary>Verify hashes for files at <paramref name="path"/>.</summary>
        /// <param name="hashes">Hashes to verify.</param>
        /// <param name="path">Directory or archive path to verify hashes at.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>Verification result.</returns>
        public static async Task<bool> VerifyAsync(IReadOnlyDictionary<string, byte[]> hashes, string path, CancellationToken cancellationToken = default)
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var root = new DirectoryInfo(path);
                return await VerifyAsync(hashes, root, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var archive = ZipFile.OpenRead(path);
                return await VerifyAsync(hashes, archive, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Verify hashes for files at <paramref name="archive"/>.</summary>
        /// <param name="hashes">Hashes to verify.</param>
        /// <param name="archive"><see cref="FileInfo"/> representing a zip archive to to verify hashes at.</param>
        /// <param name="cancellationToken"><see cref="CancellationToken"/>.</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing hashes of each of the files found.</returns>
        public static async Task<bool> VerifyAsync(IReadOnlyDictionary<string, byte[]> hashes, FileInfo archive, CancellationToken cancellationToken = default)
        {
            using var zipArchive = ZipFile.OpenRead(archive.FullName);
            return await VerifyAsync(hashes, zipArchive, cancellationToken).ConfigureAwait(false);
        }

    }
}
