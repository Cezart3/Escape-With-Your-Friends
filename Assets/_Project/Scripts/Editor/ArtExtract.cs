using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EscapeWithYourFriends.Core;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Copies the models <see cref="ArtCatalog"/> names out of the zips you downloaded, and nothing
    /// else (#79, docs/ART-PLAN.md T1).
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -logFile extract.log
    ///     -executeMethod EscapeWithYourFriends.EditorTools.ArtExtract.Run -artZips "D:\Downloads\ewyf-art"
    ///
    /// Why a command and not "unzip it into Assets": a Kenney kit is eighty models in three formats
    /// with previews and a sample scene, and the game uses six of them. A folder somebody unzipped
    /// by hand is a folder nobody can say the contents of. This one holds exactly the catalogue, the
    /// kit's colormap and the kit's own licence, and saying "the licence came from the zip" is true.
    ///
    /// Fails the run when anything is missing, and says what the zip has instead: the likeliest
    /// cause is a newer kit that renamed a file, and the fix is one line in ArtCatalog.
    /// </summary>
    public static class ArtExtract
    {
        const string DefaultZips = @"D:\Downloads\ewyf-art";

        public static void Run()
        {
            string folder = CommandLine.GetString("-artZips", DefaultZips);

            if (!Directory.Exists(folder))
            {
                Debug.LogError($"[ArtExtract] No folder {folder}. Download the kits in docs/ART-PLAN.md §3 "
                               + "into it, or pass -artZips <folder>.");
                Finish(false);
                return;
            }

            string[] zips = Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly);
            int copied = 0;
            var missing = new List<string>();

            foreach (ArtCatalog.Pack pack in ArtCatalog.Packs)
            {
                string[] wanted = ArtCatalog.Models.Where(m => m.Pack == pack.Name)
                                            .Select(m => m.File.ToLowerInvariant() + ".fbx")
                                            .Distinct().ToArray();
                if (wanted.Length == 0) continue;

                string zipPath = BestZip(zips, pack, wanted);
                if (zipPath == null)
                {
                    string named = pack.ZipHint.Length > 0 ? $"with '{pack.ZipHint}' in its name " : "";
                    missing.Add($"{pack.Author} {pack.Name}: no zip {named}holding its files in {folder} "
                                + $"({pack.Page})");
                    continue;
                }

                using ZipArchive zip = Open(zipPath);
                Directory.CreateDirectory(pack.Folder);

                var byName = zip.Entries.Where(e => e.Name.Length > 0)
                                .GroupBy(e => e.Name.ToLowerInvariant())
                                .ToDictionary(g => g.Key, g => g.ToList());

                foreach (string file in wanted)
                {
                    ZipArchiveEntry entry = Pick(byName, file);
                    if (entry == null)
                    {
                        missing.Add($"{pack.Name}/{file} is not in {Path.GetFileName(zipPath)}. "
                                    + $"Nearest: {Nearest(byName.Keys, file)}");
                        continue;
                    }

                    Copy(entry, $"{pack.Folder}/{Path.GetFileNameWithoutExtension(entry.Name)}.fbx");
                    copied++;
                }

                if (pack.Atlas)
                {
                    ZipArchiveEntry atlas = Pick(byName, pack.AtlasFile.ToLowerInvariant());
                    if (atlas == null) missing.Add($"{pack.Name}: no {pack.AtlasFile} in {Path.GetFileName(zipPath)}");
                    else Copy(atlas, $"{pack.Folder}/{pack.AtlasFile}");
                }

                if (pack.Textured)
                {
                    Directory.CreateDirectory($"{pack.Folder}/Textures");
                    foreach (ZipArchiveEntry texture in CharacterArt.Unique(zip, CharacterArt.IsTexture))
                        Copy(texture, $"{pack.Folder}/Textures/{texture.Name}");
                }

                // The licence travels with the models. A pack without one is not imported, because
                // "the page said CC0" is the one claim this project could not check (ART-PLAN §1).
                // Kenney names it License.txt; Quaternius does not always, so any licen* text counts.
                ZipArchiveEntry licence = Pick(byName, "license.txt")
                    ?? zip.Entries.FirstOrDefault(e => e.Name.ToLowerInvariant() is var n && n.StartsWith("licen")
                                                       && (n.EndsWith(".txt") || n.EndsWith(".md")));
                string beside = LicenceFor(zipPath);
                if (licence != null) Copy(licence, $"{pack.Folder}/License.txt");
                else if (File.Exists(beside)) File.Copy(beside, $"{pack.Folder}/License.txt", true);
                else missing.Add($"{pack.Name}: no licence in {Path.GetFileName(zipPath)}. Read it on {pack.Page} "
                                 + $"and, if it is CC0, save that text as {beside}");

                Debug.Log($"[ArtExtract] {pack.Author} {pack.Name} <- {Path.GetFileName(zipPath)}");
            }

            AssetDatabase.Refresh();

            foreach (string line in missing) Debug.LogError($"[ArtExtract] Missing: {line}");
            Debug.Log($"[ArtExtract] Copied {copied} model(s) into {ArtCatalog.Root}, {missing.Count} missing.");

            Finish(missing.Count == 0);
        }

        /// <summary>
        /// Where a licence goes for a zip that carries none, such as a Google Drive download: a text
        /// file beside the zip, named after it, that somebody saved from the pack's own page. Never
        /// written here - the claim has to come from a person who read the page.
        /// </summary>
        static string LicenceFor(string zipPath)
            => Path.Combine(Path.GetDirectoryName(zipPath), Path.GetFileNameWithoutExtension(zipPath) + ".License.txt");

        /// <summary>
        /// The zip for this pack: of the ones whose name mentions it, the one holding most of the
        /// files wanted. "pirate" also matches Kenney's 2D pirate pack, which holds none of them.
        /// </summary>
        static string BestZip(string[] zips, ArtCatalog.Pack pack, string[] wanted)
        {
            string best = null;
            int bestHits = 0;

            foreach (string path in zips)
            {
                if (Path.GetFileName(path).IndexOf(pack.ZipHint, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                using ZipArchive zip = Open(path);
                var names = new HashSet<string>(zip.Entries.Select(e => e.Name.ToLowerInvariant()));
                int hits = wanted.Count(names.Contains);

                if (hits <= bestHits) continue;

                best = path;
                bestHits = hits;
            }

            return best;
        }

        /// <summary>
        /// One entry by file name, wherever it sits in the zip. When a name appears more than once
        /// the copy under an "FBX" folder wins: a Kenney zip has a colormap beside each format, and
        /// the FBX's is the one its UVs were laid out against.
        /// </summary>
        static ZipArchiveEntry Pick(Dictionary<string, List<ZipArchiveEntry>> byName, string file)
        {
            if (!byName.TryGetValue(file, out List<ZipArchiveEntry> entries)) return null;

            // Quaternius ships "FBX" and "FBX (Unity)" side by side; the second is exported Y-up.
            return entries.FirstOrDefault(e => e.FullName.IndexOf("unity", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? entries.FirstOrDefault(e => e.FullName.IndexOf("fbx", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? entries[0];
        }

        // ZipArchive over a stream rather than ZipFile.OpenRead: ZipFile lives in an assembly Unity's
        // API profile has not always referenced, and this compiles against every one of them.
        internal static ZipArchive Open(string path) => new(File.OpenRead(path), ZipArchiveMode.Read);

        internal static void Copy(ZipArchiveEntry entry, string destination)
        {
            using Stream from = entry.Open();
            using FileStream to = File.Create(destination);
            from.CopyTo(to);
        }

        /// <summary>Up to five file names in the zip that share the most leading characters with the one wanted.</summary>
        static string Nearest(IEnumerable<string> names, string file)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            IEnumerable<string> close = names.Where(n => n.EndsWith(".fbx"))
                                             .OrderByDescending(n => Shared(n, stem))
                                             .Take(5);

            string list = string.Join(", ", close);
            return list.Length > 0 ? list : "(no FBX at all - is this the right zip?)";
        }

        static int Shared(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            return i;
        }

        static void Finish(bool ok)
        {
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
