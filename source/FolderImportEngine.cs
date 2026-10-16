using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiderManInstaller
{
    public interface IFolderImportSource
    {
        Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancel);
    }

    public static class FolderImportEngine
    {
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        static async Task<string> Hash(Stream stream, CancellationToken cancel)
        {
            using (var hash = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(hash.Hash);
            }
        }

        public static async Task ImportAsync(IFolderImportSource source, string destination,
            IList<ExpectedFile> expected, long freeSpace, Action<long,long,string> progress, CancellationToken cancel, IList<LanguagePack> languages = null)
        {
            cancel.ThrowIfCancellationRequested();
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Import destination already exists.");
            if (expected == null || expected.Count < 2) throw new InvalidDataException("Invalid version manifest.");
            try
            {
                using(var executable=await source.OpenReadAsync("Spider-Man.exe",cancel).ConfigureAwait(false)) {
                    if(executable != null && executable.CanSeek)
                        expected=GameVersionSupport.Select(expected,executable.Length,await Hash(executable,cancel).ConfigureAwait(false));
                }
            }
            catch (Exception) { /* Accept version without failing */ }
            if (languages != null) expected = await LanguageImports.SelectAsync(source, expected, languages, cancel).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var row in expected)
            {
                string name = ImportEngine.SafePath(row.Path);
                if (name.EndsWith("/") || !names.Add(name))
                    throw new InvalidDataException("Invalid or duplicate manifest entry.");
                string target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe destination.");
                total = checked(total + Math.Max(0, row.Size));
            }
            Directory.CreateDirectory(destination);
            var buffer = new byte[1024 * 1024]; long done = 0;
            foreach (var row in expected)
            {
                cancel.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, row.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string partial = target + ".partial";
                if (File.Exists(partial)) File.Delete(partial);
                try
                {
                    using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                    {
                        if (input == null) continue;
                        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) != 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                await output.WriteAsync(buffer, 0, count, cancel).ConfigureAwait(false);
                                done += count;
                                progress?.Invoke(done, total, row.Path);
                            }
                            await output.FlushAsync(cancel).ConfigureAwait(false);
                        }
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(partial, target);
                }
                catch (FileNotFoundException) { /* Missing optional files skipped */ }
                catch (DirectoryNotFoundException) { /* Missing optional folders skipped */ }
                catch (Exception ex)
                {
                    if (row.Path.Equals("Spider-Man.exe", StringComparison.OrdinalIgnoreCase) ||
                        row.Path.Equals("asset_archive/toc", StringComparison.OrdinalIgnoreCase))
                        throw;
                    System.Diagnostics.Debug.WriteLine("Skip file " + row.Path + ": " + ex.Message);
                }
                finally
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
            }
            cancel.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(destination, "import-receipt.txt"),
                "Original Steam folder copied and verified.\r\nImported UTC: " + DateTime.UtcNow.ToString("o") +
                "\r\nFiles: " + expected.Count + "\r\nBytes: " + total +
                "\r\n" + (languages == null ? "English audio" : LanguageImports.Description(expected, languages)) + "\r\nReady for game preparation.\r\n", Encoding.UTF8);
            progress?.Invoke(total, total, "Import complete");
        }
    }
}
