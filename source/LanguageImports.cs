using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpiderManInstaller
{
    public sealed class LanguagePack
    {
        public string Language, Label;
        public ExpectedFile File;
    }

    // Core EXE/index/archive hashes remain unchanged. Only the voice-pack set
    // varies, and each accepted pack has a packaged, pinned size and SHA-256.
    public static class LanguageImports
    {
        public static void Validate(IList<LanguagePack> packs)
        {
            if (packs == null || packs.Count == 0) throw new InvalidDataException("Language catalogue is missing.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in packs)
            {
                if (p == null || p.File == null || String.IsNullOrEmpty(p.Language) || String.IsNullOrEmpty(p.Label) ||
                    !languages.Add(p.Language) || !paths.Add(ImportEngine.SafePath(p.File.Path)) ||
                    !p.File.Path.StartsWith("asset_archive/", StringComparison.Ordinal) ||
                    p.File.Size <= 0 || p.File.Hash == null || p.File.Hash.Length != 64 || p.File.Hash.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Invalid or duplicate language pack.");
            }
        }

        public static List<ExpectedFile> Select(IList<ExpectedFile> original, IList<LanguagePack> packs,
            Func<string, long?> length)
        {
            Validate(packs);
            var audioPaths = new HashSet<string>(packs.Select(p => p.File.Path), StringComparer.OrdinalIgnoreCase);
            var result = original.Where(f => !audioPaths.Contains(f.Path)).ToList();
            int found = 0;
            foreach (var p in packs)
            {
                long? size = length(p.File.Path);
                if (!size.HasValue) continue;
                result.Add(new ExpectedFile { Path = p.File.Path, Size = size.Value, Hash = p.File.Hash });
                found++;
            }
            if (found == 0)
            {
                var defaultPack = packs.FirstOrDefault();
                if (defaultPack != null) result.Add(new ExpectedFile { Path = defaultPack.File.Path, Size = defaultPack.File.Size, Hash = defaultPack.File.Hash });
            }
            return result;
        }

        public static async Task<List<ExpectedFile>> SelectAsync(IFolderImportSource source,
            IList<ExpectedFile> original, IList<LanguagePack> packs, CancellationToken cancel)
        {
            Validate(packs);
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in packs)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    using (var input = await source.OpenReadAsync(p.File.Path, cancel).ConfigureAwait(false))
                    {
                        if (input != null && input.CanSeek)
                            lengths.Add(p.File.Path, input.Length);
                    }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception) { }
            }
            return Select(original, packs, path => lengths.ContainsKey(path) ? (long?)lengths[path] : null);
        }

        public static string Description(IList<ExpectedFile> selected, IList<LanguagePack> packs)
        {
            var paths = new HashSet<string>(selected.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var matched = packs.Where(p => paths.Contains(p.File.Path)).Select(p => p.Label).ToList();
            return "Voice packs: " + (matched.Count > 0 ? String.Join(", ", matched) : "English");
        }

        public static void ApplyToPreparation(string archiveRoot, IList<PreparationFile> rows, IList<LanguagePack> packs)
        {
            var selected = Select(new List<ExpectedFile>(), packs, path =>
            {
                string file = Path.Combine(archiveRoot, path.Substring("asset_archive/".Length));
                return File.Exists(file) ? (long?)new FileInfo(file).Length : null;
            });
            var audioPaths = new HashSet<string>(packs.Select(p => p.File.Path), StringComparer.OrdinalIgnoreCase);
            for (int i = rows.Count - 1; i >= 0; i--)
                if (rows[i].Origin == "import" && audioPaths.Contains(rows[i].Source)) rows.RemoveAt(i);
            foreach (var f in selected)
                rows.Add(new PreparationFile { Destination = f.Path, Origin = "import", Source = f.Path, Size = f.Size, Hash = f.Hash });
        }
    }
}
