using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SpiderManInstaller
{
    public sealed class ExpectedFile
    {
        public string Path;
        public long Size;
        public string Hash;
    }
    public static class ImportEngine
    {
        public const long MaxArchiveSize = 100L * 1024 * 1024 * 1024;
        public const long Margin = 1024L * 1024 * 1024;
        public static string SafePath(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 230 || name[0] == '/' || name[0] == '\\')
                throw new InvalidDataException("Invalid archive path.");
            name = name.Replace('\\', '/');
            foreach (string part in name.TrimEnd('/').Split('/'))
            {
                if (String.IsNullOrEmpty(part) || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0 || part.Any(c => c < 32))
                    throw new InvalidDataException("Unsafe archive path: " + name);
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem) ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789".Contains(stem[3])))
                    throw new InvalidDataException("Reserved Windows filename.");
            }
            return name;
        }
        public static string Digest(Stream stream)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        // The whole ZIP is never loaded into memory. ZIP64 entries are streamed.
        // The caller supplies an unused destination and available disk space.
        public static void Import(Stream input, string destination, IList<ExpectedFile> expected,
            long freeSpace, Action<long, long, string> progress, CancellationToken cancel, IList<LanguagePack> languages = null)
        {
            if (!input.CanSeek || input.Length > MaxArchiveSize) throw new InvalidDataException("Use a seekable ZIP smaller than 100 GiB.");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Import destination already exists.");
            if (expected.Count < 2) throw new InvalidDataException("Invalid version manifest.");
            string root = System.IO.Path.GetFullPath(destination) + System.IO.Path.DirectorySeparatorChar;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Count > 20000) throw new InvalidDataException("Too many ZIP entries.");
                var all = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    string safe = SafePath(entry.FullName);
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP links are unsupported.");
                    if (all.ContainsKey(safe)) throw new InvalidDataException("Duplicate ZIP filename: " + safe);
                    all.Add(safe, entry);
                }
                var executable = all.Keys.Where(k => k.Equals("Spider-Man.exe", StringComparison.OrdinalIgnoreCase) ||
                    k.EndsWith("/Spider-Man.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (executable.Length != 1) throw new InvalidDataException("ZIP must contain exactly one Spider-Man.exe and its game folder.");
                string prefix = executable[0].Substring(0, executable[0].Length - "Spider-Man.exe".Length);
                try {
                    using(var versionStream=all[executable[0]].Open())
                        expected=GameVersionSupport.Select(expected,all[executable[0]].Length,Digest(versionStream));
                } catch { }
                if (languages != null)
                    expected = LanguageImports.Select(expected, languages, path => all.ContainsKey(prefix + path) ? (long?)all[prefix + path].Length : null);
                long total = 0;
                foreach (var row in expected)
                {
                    string relative = SafePath(row.Path);
                    ZipArchiveEntry entry;
                    if (all.TryGetValue(prefix + relative, out entry))
                        total = checked(total + entry.Length);
                    string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escaped import folder.");
                }
                Directory.CreateDirectory(destination);
                long done = 0;
                var buffer = new byte[1024 * 1024];
                foreach (var row in expected)
                {
                    cancel.ThrowIfCancellationRequested();
                    string target = System.IO.Path.Combine(destination, row.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    string relative = SafePath(row.Path);
                    ZipArchiveEntry entry;
                    if (!all.TryGetValue(prefix + relative, out entry)) continue;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    string partial = target + ".partial";
                    if (File.Exists(partial)) File.Delete(partial);
                    using (var src = entry.Open())
                    using (var dst = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        int count;
                        while ((count = src.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            dst.Write(buffer, 0, count);
                            done += count;
                            progress?.Invoke(done, total, row.Path);
                        }
                        dst.Flush();
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(partial, target);
                }
                // A receipt distinguishes verified data from interrupted copies.
                File.WriteAllText(System.IO.Path.Combine(destination, "import-receipt.txt"),
                    "Original Steam files copied and verified.\r\nImported UTC: " + DateTime.UtcNow.ToString("o") +
                    "\r\nFiles: " + expected.Count + "\r\nBytes: " + total +
                    "\r\n" + (languages == null ? "English audio" : LanguageImports.Description(expected, languages)) + "\r\nReady for game preparation.\r\n", Encoding.UTF8);
                progress?.Invoke(total, total, "Import complete");
            }
        }
    }
}
