using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace RarZipProcessor
{
    internal class Program
    {
        // Optional: hard-code a path to 7z.exe or unrar.exe here if auto-detection doesn't find it.
        // e.g. "C:\\Program Files\\7-Zip\\7z.exe"
        private static string? _explicitExtractorPath = null;
        private const string SkipFileName = ".skip";

        private static int _foldersProcessed = 0;
        private static int _foldersFailed = 0;
        private static bool _useSubfolder = false;
        private static bool _dryRun = false;
        private static bool _reZip = false;
        private static string? _outputPath = null;

        private static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: RarZipProcessor <root-folder> [--subfolder] [--dry-run] [--rezip] [--output <path>] [path-to-7z-or-unrar]");
                return;
            }

            var nonFlagArgs = new List<string>();
            var outputDir = string.Empty;
            foreach (var arg in args)
            {
                if (arg.Equals("--subfolder", StringComparison.OrdinalIgnoreCase))
                {
                    _useSubfolder = true;
                }
                else if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    _dryRun = true;
                }
                else if (arg.Equals("--rezip", StringComparison.OrdinalIgnoreCase))
                {
                    _reZip = true;
                }
                else if (arg.StartsWith("--output", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the path from --output <path> or --output=<path>
                    string argValue = arg.Substring("--output".Length).Trim();
                    if (argValue.StartsWith("=", StringComparison.Ordinal) && argValue.Length > 1)
                    {
                        outputDir = argValue.Substring(1);
                    }
                    else if (argValue.Length > 0)
                    {
                        outputDir = argValue;
                    }
                    else
                    {
                        // --output with no value means we expect the next arg to be the path
                        nonFlagArgs.Add("--output");
                    }
                }
                else
                {
                    nonFlagArgs.Add(arg);
                }
            }

            // Handle --output <path> form (two separate args)
            if (string.IsNullOrEmpty(outputDir))
            {
                for (int i = 0; i < nonFlagArgs.Count - 1; i++)
                {
                    if (nonFlagArgs[i].Equals("--output", StringComparison.OrdinalIgnoreCase) ||
                        nonFlagArgs[i].Equals("--output=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < nonFlagArgs.Count)
                        {
                            string nextArg = nonFlagArgs[i + 1];
                            if (!nextArg.StartsWith("--", StringComparison.Ordinal))
                            {
                                outputDir = nextArg;
                            }
                        }
                        break;
                    }
                }
            }

            _outputPath = string.IsNullOrEmpty(outputDir) ? null : outputDir;

            if (nonFlagArgs.Count < 1)
            {
                Console.WriteLine("Usage: RarZipProcessor <root-folder> [--subfolder] [--dry-run] [--rezip] [--output <path>] [path-to-7z-or-unrar]");
                return;
            }

            string root = nonFlagArgs[0];
            if (!Directory.Exists(root))
            {
                Console.WriteLine($"Root folder does not exist: {root}");
                return;
            }

            if (nonFlagArgs.Count >= 2)
            {
                _explicitExtractorPath = nonFlagArgs[1];
            }

            string? extractorPath = ResolveExtractorPath();
            if (extractorPath == null)
            {
                Console.WriteLine("ERROR: Could not find 7z.exe or unrar.exe. Install 7-Zip (recommended) or " +
                                   "WinRAR/unrar, or pass the full path as the second argument.");
                return;
            }

            Console.WriteLine($"Using extractor: {extractorPath}");
            Console.WriteLine($"Starting at root: {root}");
            Console.WriteLine($"Subfolder mode: {_useSubfolder}");
            Console.WriteLine($"Dry-run mode: {_dryRun}");
            Console.WriteLine($"Re-zip mode: {_reZip}");
            Console.WriteLine($"Output dir: {(_outputPath ?? "same as root")}");
            Console.WriteLine();

            if (_dryRun)
            {
                PreviewDryRun(root);
            }
            else
            {
                ProcessDirectoryRecursive(root, extractorPath);
            }

            Console.WriteLine();
            if (_dryRun)
            {
                Console.WriteLine("Dry-run complete. No files were modified.");
            }
            else
            {
                Console.WriteLine($"Done. Folders processed: {_foldersProcessed}, folders failed: {_foldersFailed}");
            }
        }

        private static void PreviewDryRun(string root)
        {
            Console.WriteLine("Collecting folders with ZIP files...");
            var foldersWithZips = new List<string>();
            CollectFoldersWithZips(root, foldersWithZips);

            Console.WriteLine("Collecting folders with RAR files...");
            var foldersWithRars = new List<string>();
            CollectFoldersWithRars(root, foldersWithRars);

            int totalFolders = foldersWithZips.Count + foldersWithRars.Count;
            Console.WriteLine($"Found {foldersWithZips.Count} folder(s) with ZIP files and {foldersWithRars.Count} folder(s) with RAR files.");
            Console.WriteLine();

            if (totalFolders == 0)
            {
                Console.WriteLine("No folders with ZIP or RAR files found. Nothing to do.");
                return;
            }

            // Build a combined list of all folders (Zips + Rars) and pick a sample.
            var allFolders = new List<(string folder, string type)>();
            foreach (string f in foldersWithZips) allFolders.Add((f, "zip"));
            foreach (string f in foldersWithRars) allFolders.Add((f, "rar"));

            var random = new Random();
            var shuffled = allFolders.OrderBy(_ => random.NextDouble()).ToList();
            int previewCount = Math.Min(5, shuffled.Count);
            var previewFolders = shuffled.Take(previewCount).ToList();

            Console.WriteLine($"Previewing {previewCount} folder(s) (of {totalFolders} total: {foldersWithZips.Count} ZIP-folders, {foldersWithRars.Count} RAR-folders).");
            Console.WriteLine($"Re-zip mode: {_reZip}");
            Console.WriteLine();

            foreach (var (folder, type) in previewFolders)
            {
                if (type == "zip")
                {
                    PreviewFolder(folder);
                }
                else
                {
                    PreviewRarsInFolder(folder);
                }
            }
        }

        private static void CollectFoldersWithZips(string dir, List<string> results)
        {
            try
            {
                string[] subDirs = Directory.GetDirectories(dir);
                foreach (string sub in subDirs)
                {
                    CollectFoldersWithZips(sub, results);
                }
            }
            catch (Exception)
            {
                // Ignore permission errors or other issues.
            }

            // Skip folders with .skip sentinel file
            if (File.Exists(Path.Combine(dir, SkipFileName)))
            {
                return;
            }

            string[] zipFiles = Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly);
            if (zipFiles.Length > 0)
            {
                results.Add(dir);
            }
        }

        private static void CollectFoldersWithRars(string dir, List<string> results)
        {
            try
            {
                string[] subDirs = Directory.GetDirectories(dir);
                foreach (string sub in subDirs)
                {
                    CollectFoldersWithRars(sub, results);
                }
            }
            catch (Exception)
            {
                // Ignore permission errors or other issues.
            }

            // Skip folders with .skip sentinel file
            if (File.Exists(Path.Combine(dir, SkipFileName)))
            {
                return;
            }

            // Find all .rar files at the top level (the "main" RAR file, not part files).
            // We rely on the extractor handling any multi-part sets found in the same folder.
            string[] rarFiles = Directory.GetFiles(dir, "*.rar", SearchOption.TopDirectoryOnly);
            if (rarFiles.Length > 0)
            {
                results.Add(dir);
            }
        }

        private static void PreviewFolder(string folder)
        {
            string[] zipFiles = Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly);

            Console.WriteLine($"[DRY-RUN FOLDER] {folder}  ({zipFiles.Length} zip file(s))");
            Console.WriteLine();

            Console.WriteLine($"  -- Would extract {zipFiles.Length} ZIP file(s) into subfolders:");
            foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(zipPath);
                Console.WriteLine($"    {Path.GetFileName(zipPath)} -> {baseName}/");
            }
            Console.WriteLine();

            // Recursively expand nested ZIPs from the original archives.
            var allEntries = new List<(string zipName, string entryPath)>();
            foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string entryExt = Path.GetExtension(entry.FullName).ToLowerInvariant();
                        if (entryExt == ".zip")
                        {
                            Console.WriteLine($"    -- [nested] Expanding: {Path.GetFileName(entry.FullName)} (from {Path.GetFileName(zipPath)})");
                            // Extract this nested ZIP to a temp folder and read its contents.
                            string tempExtractDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(tempExtractDir);
                            entry.ExtractToFile(Path.Combine(tempExtractDir, Path.GetFileName(entry.FullName)), overwrite: true);
                            string nestedZipPath = Path.Combine(tempExtractDir, Path.GetFileName(entry.FullName));
                            try
                            {
                                using var nestedArchive = ZipFile.OpenRead(nestedZipPath);
                                foreach (ZipArchiveEntry nestedEntry in nestedArchive.Entries)
                                {
                                    if (nestedEntry.Length > 0)
                                    {
                                        allEntries.Add((Path.GetFileName(zipPath), nestedEntry.FullName));
                                    }
                                }
                            }
                            finally
                            {
                                SafeDeleteDirectory(tempExtractDir);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    [WARN] Could not open '{Path.GetFileName(zipPath)}': {ex.Message}");
                }
            }

            // Classify files.
            var nfoCandidates = new List<(string zipName, string entryPath)>();
            var allOtherFiles = new List<(string zipName, string entryPath)>();
            var rarVolumes = new List<(string zipName, string entryPath)>();

            Console.WriteLine($"  -- Files that would be consolidated from extracted ZIPs:");
            var skippedMetadata = new List<(string zipName, string entryPath)>();
            foreach (var (zipName, entryPath) in allEntries.OrderBy(e => e.entryPath, StringComparer.OrdinalIgnoreCase))
            {
                // Skip metadata files when re-zipping.
                string entryFileName = Path.GetFileName(entryPath).ToLowerInvariant();
                if (entryFileName == "file_id.diz" || entryFileName == "disk.id")
                {
                    if (_reZip)
                    {
                        Console.WriteLine($"    [skip] {entryPath} (metadata file)");
                        skippedMetadata.Add((zipName, entryPath));
                        continue;
                    }
                }

                string entryExt = Path.GetExtension(entryPath).ToLowerInvariant();
                bool isNfo = entryExt.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                bool isRar = IsRarVolumeFromPath(entryPath);

                string tag = isNfo ? " [NFO]" : isRar ? " [RAR]" : "";
                string showPath = _useSubfolder && isRar ? entryPath : Path.GetFileName(entryPath);
                Console.WriteLine($"    {Path.GetFileName(zipName)}/{showPath} -> {(isRar && _useSubfolder ? "subfolder path: " + entryPath : "flattened: " + Path.GetFileName(entryPath))}{tag}");

                if (isNfo)
                {
                    nfoCandidates.Add((zipName, entryPath));
                }
                else if (isRar)
                {
                    rarVolumes.Add((zipName, entryPath));
                }
                else
                {
                    allOtherFiles.Add((zipName, entryPath));
                }
            }

            if (skippedMetadata.Count > 0 && _reZip)
            {
                Console.WriteLine($"    (skipped {skippedMetadata.Count} metadata file(s) — FILE_ID.DIZ/DISK.ID)");
            }

            if (allOtherFiles.Count == 0 && rarVolumes.Count == 0 && nfoCandidates.Count == 0)
            {
                Console.WriteLine($"    (no files found in ZIPs)");
            }

            Console.WriteLine();

            // Show RAR volume detection.
            Console.WriteLine($"  -- RAR volumes found: {rarVolumes.Count}");
            if (rarVolumes.Count > 0)
            {
                string firstRar = GetFirstRarFromPaths(rarVolumes.Select(rv => rv.entryPath));
                string outputDir = _outputPath ?? folder;
                Console.WriteLine($"    First volume would be: {firstRar}");
                Console.WriteLine($"    RAR extraction would output to: {outputDir}/");
                Console.WriteLine();

                Console.WriteLine($"    RAR files found:");
                foreach (var (zipName, entryPath) in rarVolumes)
                {
                    Console.WriteLine($"      {Path.GetFileName(zipName)}/{entryPath}");
                }
            }
            else
            {
                Console.WriteLine($"    (none — no RAR extraction step would occur)");
            }

            Console.WriteLine();

            // Show NFO handling.
            Console.WriteLine($"  -- NFO files found: {nfoCandidates.Count}");
            if (nfoCandidates.Count > 0)
            {
                Console.WriteLine($"    Would keep one (first): {nfoCandidates[0].entryPath}");
                Console.WriteLine($"    Would discard {(nfoCandidates.Count - 1)} other(s).");
            }

            Console.WriteLine();

            // Show other files consolidation.
            Console.WriteLine($"  -- Other files consolidated (not RAR/NFO): {allOtherFiles.Count}");

            Console.WriteLine($"  -- Cleanup: per-ZIP extraction folders and temp staging folder would be deleted.");

            // Show re-zip information when --reZip is set.
            if (_reZip)
            {
                Console.WriteLine();

                // Group files by their source ZIP.
                var filesByZip = allEntries.GroupBy(e => e.zipName)
                    .ToDictionary(g => g.Key, g => g.ToList());

                Console.WriteLine($"  -- Re-zip: all {zipFiles.Length} folder(s) would be re-zipped to {(_outputPath ?? "root folder")}");
                string outputDir = _outputPath ?? folder;
                string folderName = Path.GetFileName(folder);
                Console.WriteLine($"    {folderName}.zip -> {folderName}.zip");
                Console.WriteLine($"      (files from {zipFiles.Length} ZIP file(s))");

                // Show actual files that would be in the re-zipped version.
                var allFilesForRezip = new List<(string zipName, string entryPath)>();
                var skippedMetaCount = 0;
                foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var archive = ZipFile.OpenRead(zipPath);
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            // Skip metadata files when re-zipping.
                            string entryFileName = Path.GetFileName(entry.FullName).ToLowerInvariant();
                            if (entryFileName == "file_id.diz" || entryFileName == "disk.id")
                            {
                                skippedMetaCount++;
                                continue;
                            }

                            if (entry.Length > 0) // Skip empty directories.
                            {
                                allFilesForRezip.Add((Path.GetFileName(zipPath), entry.FullName));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    [WARN] Could not open '{Path.GetFileName(zipPath)}': {ex.Message}");
                    }
                }

                if (allFilesForRezip.Count == 0)
                {
                    Console.WriteLine($"      (no files found in ZIPs)");
                }
                else
                {
                    foreach (var (zipName, entryPath) in allFilesForRezip.OrderBy(e => e.entryPath, StringComparer.OrdinalIgnoreCase).Take(10))
                    {
                        string tag = Path.GetExtension(entryPath).ToLowerInvariant().Equals(".nfo", StringComparison.OrdinalIgnoreCase) ? " [NFO]" : "";
                        string showPath = _useSubfolder ? entryPath : Path.GetFileName(entryPath);
                        Console.WriteLine($"        {showPath}{tag}");
                    }
                    if (allFilesForRezip.Count > 10)
                    {
                        Console.WriteLine($"        ... and {(allFilesForRezip.Count - 10)} more");
                    }
                    if (skippedMetaCount > 0)
                    {
                        Console.WriteLine($"      (skipped {skippedMetaCount} metadata file(s) — FILE_ID.DIZ/DISK.ID)");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("---");
            Console.WriteLine();
        }

        private static void PreviewRarsInFolder(string folder)
        {
            // Find all .rar files at the top level.
            string[] rarFiles = Directory.GetFiles(folder, "*.rar", SearchOption.TopDirectoryOnly);
            if (rarFiles.Length == 0)
            {
                return; // Nothing to do.
            }

            Console.WriteLine($"[DRY-RUN FOLDER] {folder}  ({rarFiles.Length} rar file(s))");
            Console.WriteLine();

            Console.WriteLine($"  -- Would process {rarFiles.Length} RAR file(s):");
            foreach (string rarPath in rarFiles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    {Path.GetFileName(rarPath)}");
            }
            Console.WriteLine();

            // Try to preview the contents of the first RAR to show what would be in the output ZIP.
            if (rarFiles.Length > 0)
            {
                string firstRar = rarFiles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).First();
                Console.WriteLine($"  -- Previewing contents from first RAR: {Path.GetFileName(firstRar)}");

                // Use 7z/unrar to extract to a temp folder for preview.
                string? extractorPath = ResolveExtractorPath();
                if (extractorPath != null)
                {
                    string tempExtractDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                    try
                    {
                        Console.WriteLine($"    Extracting to temp folder...");
                        Directory.CreateDirectory(tempExtractDir);

                        bool isSevenZip = Path.GetFileNameWithoutExtension(extractorPath)
                            .Equals("7z", StringComparison.OrdinalIgnoreCase);

                        string outputDir = Path.Combine(tempExtractDir, "_extracted");
                        Directory.CreateDirectory(outputDir);

                        string args = isSevenZip
                            ? $"x \"{firstRar}\" -o\"{outputDir}\" -y"
                            : $"x -y \"{firstRar}\" \"{outputDir}{Path.DirectorySeparatorChar}\"";

                        var psi = new ProcessStartInfo
                        {
                            FileName = extractorPath,
                            Arguments = args,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };

                        using var process = Process.Start(psi);
                        if (process != null)
                        {
                            string stdout = process.StandardOutput.ReadToEnd();
                            string stderr = process.StandardError.ReadToEnd();
                            process.WaitForExit();

                            if (process.ExitCode == 0)
                            {
                                var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
                                Console.WriteLine($"    Would create: {Path.GetFileName(folder)}.zip ({files.Length} file(s))");
                                Console.WriteLine();
                                Console.WriteLine($"    -- Files that would be in the output ZIP:");
                                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(5))
                                {
                                    string showPath = _useSubfolder
                                        ? Path.GetRelativePath(outputDir, file)
                                        : Path.GetFileName(file);
                                    Console.WriteLine($"      {showPath}");
                                }
                                if (files.Length > 5)
                                {
                                    Console.WriteLine($"      ... and {files.Length - 5} more");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"    [WARN] Could not extract first RAR for preview (exit code {process.ExitCode}).");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"    [WARN] Failed to start extractor.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    [WARN] Preview extraction failed: {ex.Message}");
                    }
                    finally
                    {
                        SafeDeleteDirectory(tempExtractDir);
                    }
                }
                else
                {
                    Console.WriteLine($"    [WARN] No extractor found for preview.");
                }
            }

            Console.WriteLine($"  -- Output would be: {Path.Combine(_outputPath ?? folder, $"{Path.GetFileName(folder)}.zip")}");
            Console.WriteLine();
            Console.WriteLine("---");
            Console.WriteLine();
        }

        private static bool IsRarVolumeFromPath(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".rar")
                return true;
            if (ext.Length >= 2 && ext[1] >= '0' && ext[1] <= '9')
                return true;
            string fileName = Path.GetFileName(path);
            return Regex.IsMatch(fileName, @"\.part\d+\.rar$", RegexOptions.IgnoreCase);
        }

        private static string GetFirstRarFromPaths(IEnumerable<string> rarPaths)
        {
            // Newer style: name.part1.rar / name.part01.rar.
            var partStyle = new List<string>();
            foreach (string p in rarPaths)
            {
                if (Regex.IsMatch(Path.GetFileName(p), @"\.part0*1\.rar$", RegexOptions.IgnoreCase))
                {
                    partStyle.Add(p);
                }
            }
            partStyle.Sort(StringComparer.OrdinalIgnoreCase);
            if (partStyle.Count > 0)
            {
                return partStyle[0];
            }

            // Old style: the first volume is the plain ".rar" file.
            var plainRar = new List<string>();
            foreach (string p in rarPaths)
            {
                if (Path.GetExtension(p).Equals(".rar", StringComparison.OrdinalIgnoreCase))
                {
                    plainRar.Add(p);
                }
            }
            plainRar.Sort(StringComparer.OrdinalIgnoreCase);
            if (plainRar.Count > 0)
            {
                return plainRar[0];
            }

            // Fallback: alphabetically first.
            var sorted = new List<string>(rarPaths);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted.FirstOrDefault() ?? "";
        }

        /// <summary>
        /// Recursively walks the directory tree. Subdirectories are captured up-front so that
        /// any temporary folders created/removed while processing zips in the current folder
        /// don't interfere with the recursion.
        /// </summary>
        private static void ProcessDirectoryRecursive(string dir, string extractorPath)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not enumerate subdirectories of '{dir}': {ex.Message}");
                subDirs = Array.Empty<string>();
            }

            try
            {
                ProcessZipsInFolder(dir, extractorPath);
            }
            catch (Exception ex)
            {
                _foldersFailed++;
                Console.WriteLine($"  [ERROR] Failed processing '{dir}' (ZIP): {ex.Message}");
            }

            // Also process RAR files in this folder.
            try
            {
                ProcessRarsInFolder(dir, extractorPath);
            }
            catch (Exception ex)
            {
                _foldersFailed++;
                Console.WriteLine($"  [ERROR] Failed processing '{dir}' (RAR): {ex.Message}");
            }

            foreach (string sub in subDirs)
            {
                // Skip in case it was removed as part of cleanup (shouldn't normally happen,
                // since only folders we create ourselves get removed, but be defensive).
                if (Directory.Exists(sub))
                {
                    ProcessDirectoryRecursive(sub, extractorPath);
                }
            }
        }

        private static void ProcessZipsInFolder(string folder, string extractorPath)
        {
            // Skip folders with .skip sentinel file
            if (File.Exists(Path.Combine(folder, SkipFileName)))
            {
                Console.WriteLine($"  [skip] {folder} (contains {SkipFileName} file, skipping folder)");
                return;
            }

            string[] zipFiles = Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly);
            if (zipFiles.Length == 0)
            {
                return; // Nothing to do in this folder.
            }

            Console.WriteLine($"[FOLDER] {folder}  ({zipFiles.Length} zip file(s))");

            var extractedZipFolders = new List<string>();
            string partsDir = Path.Combine(folder, "_rzp_parts_" + Guid.NewGuid().ToString("N"));
            var nfoCandidates = new List<string>();

            try
            {
                Directory.CreateDirectory(partsDir);

                // 1. Extract every zip into its own temp folder named after the zip.
                foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
                {
                    string baseName = Path.GetFileNameWithoutExtension(zipPath);
                    string zipExtractDir = Path.Combine(folder, baseName);

                    // Avoid colliding with an existing folder of the same name.
                    int suffix = 1;
                    while (Directory.Exists(zipExtractDir))
                    {
                        zipExtractDir = Path.Combine(folder, $"{baseName}_{suffix++}");
                    }

                    Console.WriteLine($"  Extracting zip: {Path.GetFileName(zipPath)} -> {Path.GetFileName(zipExtractDir)}");
                    ZipFile.ExtractToDirectory(zipPath, zipExtractDir);
                    extractedZipFolders.Add(zipExtractDir);
                }

                // 2. Move all files out of the per-zip folders. Rar volumes go into partsDir;
                //    nfo files are set aside so we can keep just one.
                // Track RAR files by source folder for later extraction re-placement.
                var rarSourceMapping = new Dictionary<string, List<(string relativePath, string zipExtractDir)>>();

                if (_reZip)
                {
                    // When re-zipping, move only RAR volumes to partsDir,
                    // move non-RAR files back to their original subfolder.
                    foreach (string zipExtractDir in extractedZipFolders)
                    {
                        foreach (string file in Directory.GetFiles(zipExtractDir, "*", SearchOption.AllDirectories))
                        {
                            string ext = Path.GetExtension(file);
                            if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                            {
                                nfoCandidates.Add(file);
                                continue;
                            }

                             if (IsRarVolume(file))
                            {
                                // RAR volume - move to partsDir for extraction.
                                // Track source folder for later re-placement.
                                string relativePath = _useSubfolder
                                    ? Path.GetRelativePath(zipExtractDir, file)
                                    : Path.GetFileName(file);
                                rarSourceMapping.TryGetValue(zipExtractDir, out var existingList);
                                if (existingList == null)
                                {
                                    existingList = new List<(string relativePath, string zipExtractDir)>();
                                }
                                existingList.Add((relativePath, zipExtractDir));
                                rarSourceMapping[zipExtractDir] = existingList;

                                string destPath = Path.Combine(partsDir, relativePath);
                                destPath = MakeUniquePath(destPath);
                                string? parentDir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(parentDir) && parentDir != partsDir && !Directory.Exists(parentDir))
                                {
                                    Directory.CreateDirectory(parentDir);
                                }
                                File.Move(file, destPath);
                            }
                            else
                            {
                                // Non-RAR file.
                                string? targetOutputDir = _outputPath ?? folder;
                                bool preserveSubfolder = _useSubfolder && _outputPath != null;
                                string relativePath = preserveSubfolder
                                    ? Path.GetRelativePath(zipExtractDir, file)
                                    : Path.GetFileName(file);
                                string destPath;
                                if (_reZip)
                                {
                                    // When re-zipping, move back to subfolder (will be zipped up).
                                    destPath = Path.Combine(zipExtractDir, relativePath);
                                    destPath = MakeUniquePath(destPath);
                                    string? parentDir = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(parentDir) && parentDir != zipExtractDir && !Directory.Exists(parentDir))
                                    {
                                        Directory.CreateDirectory(parentDir);
                                    }
                                }
                                else
                                {
                                    // When not re-zipping, move to output directory (or flat).
                                    destPath = Path.Combine(targetOutputDir, relativePath);
                                    destPath = MakeUniquePath(destPath);
                                    string? parentDir = Path.GetDirectoryName(destPath);
                                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                                    {
                                        Directory.CreateDirectory(parentDir);
                                    }
                                }
                                File.Move(file, destPath);
                            }
                        }

                        // Store mapping if RAR files were found.
                        // (Mapping is already maintained above via rarSourceMapping)
                    }
                }
                else
                {
                    // Original flat behavior.
                    foreach (string zipExtractDir in extractedZipFolders)
                    {
                        foreach (string file in Directory.GetFiles(zipExtractDir, "*", SearchOption.AllDirectories))
                        {
                            string ext = Path.GetExtension(file);
                            if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                            {
                                nfoCandidates.Add(file);
                                continue;
                            }

                            string relativePath = _useSubfolder && IsRarVolume(file)
                                ? Path.GetRelativePath(zipExtractDir, file)
                                : Path.GetFileName(file);
                            string destPath = Path.Combine(partsDir, relativePath);
                            destPath = MakeUniquePath(destPath);
                            string? parentDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(parentDir) && parentDir != partsDir && !Directory.Exists(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }
                            File.Move(file, destPath);
                        }
                    }
                }

                // 3. Locate the first volume of the multi-part rar set.
                string[] rarParts = Directory.GetFiles(partsDir, "*.r*", SearchOption.TopDirectoryOnly);
                string outputDir = _outputPath ?? folder;
                Console.WriteLine($"  [debug] outputDir = '{outputDir}', outputPath = '{(_outputPath ?? "null")}'");
                if (rarParts.Length == 0)
                {
                    Console.WriteLine("  [WARN] No rar parts found after extracting zips. Skipping extraction step.");
                }
                else
                {
                    string mainRar = FindFirstRarVolume(rarParts);
                    Console.WriteLine($"  Extracting rar set starting at: {Path.GetFileName(mainRar)}");
                    RunRarExtraction(extractorPath, mainRar, outputDir);

                    // Re-place extracted files back into their source subfolders.
                    // When --output is set, leave extracted files in output directory.
                    if (_outputPath == null)
                    {
                        Console.WriteLine($"  Re-placing extracted files back into source subfolders...");
                        foreach (var kvp in rarSourceMapping)
                        {
                            string sourceFolder = kvp.Key;
                            // Get all files in outputDir (the extraction output).
                            foreach (string extractedFile in Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))
                            {
                                // Find matching RAR source to determine destination.
                                // Use the RAR relative path to determine where extracted files should go.
                                // For simplicity, move all extracted files into the source folder.
                                string relativePath = Path.GetRelativePath(outputDir, extractedFile);
                                string destPath = Path.Combine(sourceFolder, relativePath);
                                destPath = MakeUniquePath(destPath);
                                string? parentDir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                                {
                                    Directory.CreateDirectory(parentDir);
                                }
                                Console.WriteLine($"    [replace] {Path.GetFileName(extractedFile)} -> {Path.GetFileName(sourceFolder)}/");
                                File.Move(extractedFile, destPath);
                            }
                        }
                    }
                }

                // 4. Keep exactly one nfo file (prefer one we set aside; otherwise whatever
                //    the rar extraction may have produced directly into 'outputDir').
                nfoCandidates.AddRange(
                    Directory.GetFiles(outputDir, "*.nfo", SearchOption.TopDirectoryOnly));

                KeepOneNfo(outputDir, nfoCandidates);

                // 5. Cleanup: remove the original zips, the per-zip extraction folders, and partsDir.
                //foreach (string zipPath in zipFiles)
                //{
                //    SafeDelete(zipPath);
                //}

                if (_reZip)
                {
                    // When re-zipping, merge all per-zip subfolders into one ZIP named after the folder.
                    string? rezipOutputDir = _outputPath ?? folder;
                    string folderName = Path.GetFileName(folder);
                    string zipPath = Path.Combine(rezipOutputDir, $"{folderName}.zip");
                    Console.WriteLine($"  [debug] Output directory: {(_outputPath ?? "same as source folder")}");
                    Console.WriteLine($"  [debug] Output ZIP path: {zipPath}");
                    Console.WriteLine($"  [debug] Re-zipping all subfolders -> {Path.GetFileName(zipPath)}");

                        // Collect all files from all subfolders into a temporary staging folder.
                        string stagingDir = Path.Combine(folder, "_rzp_staging_" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            Directory.CreateDirectory(stagingDir);
                            foreach (string zipExtractDir in extractedZipFolders)
                            {
                                Console.WriteLine($"  Processing files from: {Path.GetFileName(zipExtractDir)}");
                                // Move files from each subfolder into staging, preserving structure when --subfolder.
                                foreach (string file in Directory.GetFiles(zipExtractDir, "*", SearchOption.AllDirectories))
                                {
                                    string fileName = Path.GetFileName(file).ToLowerInvariant();
                                    // Skip metadata files that are common across ZIPs and would just overwrite each other.
                                    if (fileName == "file_id.diz" || fileName == "disk.id")
                                    {
                                        Console.WriteLine($"    [skip] {Path.GetFileName(file)} (metadata file, excluded from re-zip)");
                                        continue;
                                    }

                                     string ext = Path.GetExtension(file).ToLowerInvariant();
                                      if (ext == ".zip")
                                      {
                                          // Recursively extract this ZIP and collect its contents.
                                          Console.WriteLine($"    [recursive] Extracting nested ZIP: {Path.GetFileName(file)}");
                                          ExtractZipRecursive(file, stagingDir, extractorPath);
                                      }
                                     else if (IsRarVolume(file))
                                     {
                                         // RAR volume - move to partsDir for extraction.
                                         string relativePath = _useSubfolder
                                             ? Path.GetRelativePath(zipExtractDir, file)
                                             : Path.GetFileName(file);
                                         string destPath = Path.Combine(partsDir, relativePath);
                                         destPath = MakeUniquePath(destPath);
                                         string? parentDir = Path.GetDirectoryName(destPath);
                                         if (!string.IsNullOrEmpty(parentDir) && parentDir != partsDir && !Directory.Exists(parentDir))
                                         {
                                             Directory.CreateDirectory(parentDir);
                                         }
                                         Console.WriteLine($"    [rar-extract] Moving to RAR extraction: {Path.GetFileName(file)}");
                                         File.Move(file, destPath);
                                     }
                                     else
                                     {
                                         string relativePath = _useSubfolder
                                             ? Path.GetRelativePath(zipExtractDir, file)
                                             : Path.GetFileName(file);
                                         string destPath = Path.Combine(stagingDir, relativePath);
                                         destPath = MakeUniquePath(destPath);
                                         string? parentDir = Path.GetDirectoryName(destPath);
                                         if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                                         {
                                             Directory.CreateDirectory(parentDir);
                                         }
                                         File.Move(file, destPath);
                                     }
                                }
                                // Delete the individual subfolder.
                                SafeDeleteDirectory(zipExtractDir);
                            }

                            // When --output is set, also collect files from output directory.
                            if (_outputPath != null)
                            {
                                Console.WriteLine($"  Processing files from output directory: {outputDir}");
                                // Move files from output directory into staging.
                                foreach (string file in Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))
                                {
                                    string fileName = Path.GetFileName(file).ToLowerInvariant();
                                    // Skip metadata files that are common across ZIPs and would just overwrite each other.
                                    if (fileName == "file_id.diz" || fileName == "disk.id")
                                    {
                                        Console.WriteLine($"    [skip] {Path.GetFileName(file)} (metadata file, excluded from re-zip)");
                                        continue;
                                    }

                                    string ext = Path.GetExtension(file).ToLowerInvariant();
                                    if (ext == ".zip")
                                    {
                                        // Recursively extract this ZIP and collect its contents.
                                        Console.WriteLine($"    [recursive] Extracting nested ZIP: {Path.GetFileName(file)}");
                                        ExtractZipRecursive(file, stagingDir, extractorPath);
                                    }
                                    else if (IsRarVolume(file))
                                    {
                                        // RAR volume - move to partsDir for extraction.
                                        string relativePath = _useSubfolder
                                            ? Path.GetRelativePath(outputDir, file)
                                            : Path.GetFileName(file);
                                        string destPath = Path.Combine(partsDir, relativePath);
                                        destPath = MakeUniquePath(destPath);
                                        string? parentDir = Path.GetDirectoryName(destPath);
                                        if (!string.IsNullOrEmpty(parentDir) && parentDir != partsDir && !Directory.Exists(parentDir))
                                        {
                                            Directory.CreateDirectory(parentDir);
                                        }
                                        Console.WriteLine($"    [rar-extract] Moving to RAR extraction: {Path.GetFileName(file)}");
                                        File.Move(file, destPath);
                                    }
                                    else
                                    {
                                        // Non-RAR, non-ZIP file.
                                        string relativePath = _useSubfolder
                                            ? Path.GetRelativePath(outputDir, file)
                                            : Path.GetFileName(file);
                                        string destPath = Path.Combine(stagingDir, relativePath);
                                        destPath = MakeUniquePath(destPath);
                                        string? parentDir = Path.GetDirectoryName(destPath);
                                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                                        {
                                            Directory.CreateDirectory(parentDir);
                                        }
                                        File.Move(file, destPath);
                                    }
                                }
                            }

                            // Create the merged ZIP.
                            CreateZipFromFolder(stagingDir, zipPath);
                        }
                        finally
                        {
                            SafeDeleteDirectory(stagingDir);
                        }
                }
                else
                {
                    foreach (string zipExtractDir in extractedZipFolders)
                    {
                        SafeDeleteDirectory(zipExtractDir);
                    }
                }

                SafeDeleteDirectory(partsDir);

                _foldersProcessed++;
                Console.WriteLine("  Done.");
            }
            catch (Exception)
            {
                Console.WriteLine($"  [ERROR] Leaving temporary files in place for manual inspection in '{folder}'.");
                throw;
            }
        }

        /// <summary>
        /// Finds all .rar files in a folder, extracts them, and creates a single
        /// output ZIP.  Handles single .rar files as well as multi-part sets
        /// (the extractor handles .r00/.r01/.part1.rar, etc., automatically).
        /// </summary>
        private static void ProcessRarsInFolder(string folder, string extractorPath)
        {
            // Skip folders with .skip sentinel file.
            if (File.Exists(Path.Combine(folder, SkipFileName)))
            {
                Console.WriteLine($"  [skip] {folder} (contains {SkipFileName} file, skipping folder)");
                return;
            }

            // Find all .rar files at the top level.
            string[] rarFiles = Directory.GetFiles(folder, "*.rar", SearchOption.TopDirectoryOnly);
            if (rarFiles.Length == 0)
            {
                return; // Nothing to do.
            }

            Console.WriteLine($"[FOLDER] {folder}  ({rarFiles.Length} rar file(s))");

            // Staging directory where all extracted files will be collected.
            string stagingDir = Path.Combine(folder, "_rzp_rar_staging_" + Guid.NewGuid().ToString("N"));
            string? targetOutputDir = _outputPath ?? folder;

            try
            {
                Directory.CreateDirectory(stagingDir);

                // Extract each .rar into the staging directory.
                foreach (string rarFile in rarFiles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  Extracting RAR: {Path.GetFileName(rarFile)}");

                    // Each RAR gets its own subdirectory inside staging to avoid collisions.
                    string rarExtractDir = Path.Combine(stagingDir, Path.GetFileNameWithoutExtension(rarFile));
                    Directory.CreateDirectory(rarExtractDir);

                    RunRarExtraction(extractorPath, rarFile, rarExtractDir);

                    // Move extracted files to the staging directory.
                    // Files are placed in <stagingDir>/<RAR_NAME>_files/ to avoid collisions between
                    // different RARs that contain files with the same names (e.g., disk1.img from
                    // multiple RARs).
                    // IMPORTANT: Subfolder goes at stagingDir root, NOT inside rarExtractDir, because
                    // SafeDeleteDirectory(rarExtractDir) will remove everything inside it.
                    int moved = 0;
                    foreach (string extractedFile in Directory.GetFiles(rarExtractDir, "*", SearchOption.AllDirectories))
                    {
                        string rarSubfolder = Path.GetFileNameWithoutExtension(rarFile) + "_files";
                        string relativePath = _useSubfolder
                            ? Path.Combine(rarSubfolder, Path.GetFileName(extractedFile))
                            : Path.GetFileName(extractedFile);
                        string destPath = Path.Combine(stagingDir, relativePath);
                        destPath = MakeUniquePath(destPath);

                        string? parentDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        File.Move(extractedFile, destPath);
                        moved++;
                    }

                    SafeDeleteDirectory(rarExtractDir);
                }

                // Create the output ZIP from the staging directory.
                string folderName = Path.GetFileName(folder);
                string zipPath = Path.Combine(targetOutputDir, $"{folderName}.zip");
                Console.WriteLine($"  Creating ZIP: {Path.GetFileName(zipPath)} from staging");

                Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? ".");
                ZipFile.CreateFromDirectory(stagingDir, zipPath,
                    System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);

                Console.WriteLine("  Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] Leaving temporary files in place for manual inspection in '{folder}'.");
                Console.WriteLine($"  Error: {ex.Message}");
                throw;
            }
            finally
            {
                SafeDeleteDirectory(stagingDir);
                _foldersProcessed++;
            }
        }

        private static bool IsRarVolume(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".rar")
                return true;
            if (ext.Length >= 2 && ext[1] >= '0' && ext[1] <= '9')
                return true;
            if (Regex.IsMatch(Path.GetFileName(filePath), @"\.part\d+\.rar$", RegexOptions.IgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// Picks the first volume of a multi-part rar set. Handles both the old-style naming
        /// (name.rar, name.r00, name.r01, ...) and the newer "partN" style (name.part1.rar, ...).
        /// </summary>
        private static string FindFirstRarVolume(string[] rarParts)
        {
            // Newer style: name.part1.rar / name.part01.rar (first volume must end in .rar).
            var partStyle = rarParts
                .Where(p => Regex.IsMatch(Path.GetFileName(p), @"\.part0*1\.rar$", RegexOptions.IgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (partStyle.Count > 0)
            {
                return partStyle[0];
            }

            // Old style: the first volume is the plain ".rar" file; continuation volumes are
            // ".r00", ".r01", etc.
            var plainRar = rarParts
                .Where(p => Path.GetExtension(p).Equals(".rar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (plainRar.Count > 0)
            {
                return plainRar[0];
            }

            // Fallback: just take the alphabetically first part and let the extractor figure it out.
            return rarParts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
        }

        private static void RunRarExtraction(string extractorPath, string mainRarPath, string destDir)
        {
            Directory.CreateDirectory(destDir);

            bool isSevenZip = Path.GetFileNameWithoutExtension(extractorPath)
                .Equals("7z", StringComparison.OrdinalIgnoreCase);

            string arguments = isSevenZip
                ? $"x \"{mainRarPath}\" -o\"{destDir}\" -y"
                : $"x -y \"{mainRarPath}\" \"{destDir}{Path.DirectorySeparatorChar}\"";

            var psi = new ProcessStartInfo
            {
                FileName = extractorPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start extractor process: {extractorPath}");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Extractor exited with code {process.ExitCode} for '{mainRarPath}'.\n{stdout}\n{stderr}");
            }
        }

        private static void KeepOneNfo(string folder, List<string> nfoCandidates)
        {
            var distinct = nfoCandidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinct.Count == 0)
            {
                return;
            }

            string keeper = distinct[0];

            // Make sure the one we keep actually lives in the destination folder (not a temp
            // per-zip folder that's about to be deleted).
            string keeperDest = Path.Combine(folder, Path.GetFileName(keeper));
            if (!string.Equals(Path.GetFullPath(keeper), Path.GetFullPath(keeperDest), StringComparison.OrdinalIgnoreCase))
            {
                keeperDest = MakeUniquePath(keeperDest);
                File.Move(keeper, keeperDest);
                keeper = keeperDest;
            }

            foreach (string extra in distinct.Skip(1))
            {
                if (!string.Equals(Path.GetFullPath(extra), Path.GetFullPath(keeper), StringComparison.OrdinalIgnoreCase))
                {
                    SafeDelete(extra);
                }
            }
        }

        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            string dir = Path.GetDirectoryName(path) ?? ".";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}_{i}{ext}");
                i++;
            } while (File.Exists(candidate));

            return candidate;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete file '{path}': {ex.Message}");
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete folder '{path}': {ex.Message}");
            }
        }

        private static string? ResolveExtractorPath()
        {
            if (!string.IsNullOrEmpty(_explicitExtractorPath) && File.Exists(_explicitExtractorPath))
            {
                return _explicitExtractorPath;
            }

            string[] candidates =
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                @"C:\Program Files\WinRAR\UnRAR.exe",
                @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
                @"C:\Program Files\WinRAR\Rar.exe",
                @"C:\Program Files (x86)\WinRAR\Rar.exe",
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Last resort: hope one of these is on PATH.
            foreach (string exeName in new[] { "7z.exe", "7z", "UnRAR.exe", "unrar" })
            {
                if (IsOnPath(exeName))
                {
                    return exeName;
                }
            }

            return null;
        }

        private static bool IsOnPath(string exeName)
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return false;
            }

            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string full = Path.Combine(dir, exeName);
                    if (File.Exists(full))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            return false;
        }

        private static void CreateZipFromFolder(string sourceFolder, string outputZipPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath) ?? ".");
            ZipFile.CreateFromDirectory(sourceFolder, outputZipPath, compressionLevel: System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
        }

        // NOTE: Each recursive level uses a subdirectory named after the source ZIP to preserve
        // the nested hierarchy in the final archive. Without this, files from all nesting levels
        // would be flattened into the same staging directory, causing collisions.
        private static List<string> ExtractZipRecursive(string zipPath, string baseStagingDir, string? extractorPath, int depth = 0)
        {
            var collected = new List<string>();
            int indent = depth * 2;
            string prefix = new(' ', indent);
            string zipName = Path.GetFileNameWithoutExtension(zipPath);
            string localStagingDir = Path.Combine(baseStagingDir, zipName);

            try
            {
                string tempExtractDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempExtractDir);
                Console.WriteLine($"{prefix}{new string('─', indent)}[recursive] Extracting: {Path.GetFileName(zipPath)}");

                ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

                // Per-ZIP subdirectory so files from different nested archives don't collide.
                Directory.CreateDirectory(localStagingDir);

                // Collect RAR files for extraction.
                var rarFiles = new List<string>();

                foreach (string file in Directory.GetFiles(tempExtractDir, "*", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileName(file).ToLowerInvariant();
                    // Skip metadata files that are common across ZIPs and would just overwrite each other.
                    if (fileName == "file_id.diz" || fileName == "disk.id")
                    {
                        Console.WriteLine($"{prefix}{new string('─', indent + 2)}[skip] {Path.GetFileName(file)} (metadata)");
                        continue;
                    }

                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".zip")
                    {
                        Console.WriteLine($"{prefix}{new string('─', indent)}[recursive] Found nested ZIP: {Path.GetFileName(file)}");
                        var nested = ExtractZipRecursive(file, localStagingDir, extractorPath, depth + 1);
                        collected.AddRange(nested);
                    }
                    else if (IsRarVolume(file))
                    {
                        Console.WriteLine($"{prefix}{new string('─', indent + 2)}[nested-rar] Found RAR: {Path.GetFileName(file)}");
                        rarFiles.Add(file);
                    }
                    else
                    {
                        // Move the extracted file into staging, preserving subfolder structure.
                        string relativePath = _useSubfolder
                            ? Path.GetRelativePath(tempExtractDir, file)
                            : Path.GetFileName(file);
                        string destPath = Path.Combine(localStagingDir, relativePath);
                        destPath = MakeUniquePath(destPath);
                        string? parentDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        File.Move(file, destPath);
                        collected.Add(destPath);
                        Console.WriteLine($"{prefix}{new string('─', indent + 2)}→ {relativePath}");
                    }
                }

                // Handle nested subdirectories - check for ZIPs and RARs
                foreach (string subDir in Directory.GetDirectories(tempExtractDir, "*", SearchOption.AllDirectories))
                {
                    // Check for nested ZIPs in subdirectories
                    foreach (string nestedZip in Directory.GetFiles(subDir, "*.zip", SearchOption.TopDirectoryOnly))
                    {
                        Console.WriteLine($"{prefix}{new string('─', indent)}[recursive] Found nested ZIP in subfolder: {Path.GetFileName(nestedZip)}");
                        var nested = ExtractZipRecursive(nestedZip, localStagingDir, extractorPath, depth + 1);
                        collected.AddRange(nested);
                    }

                    // Check for RARs in subdirectories
                    foreach (string rarFile in Directory.GetFiles(subDir, "*.rar", SearchOption.TopDirectoryOnly))
                    {
                        Console.WriteLine($"{prefix}{new string('─', indent + 2)}[nested-rar] Found RAR in subfolder: {Path.GetFileName(rarFile)}");
                        rarFiles.Add(rarFile);
                    }

                    // Check for numbered RAR extensions (r00, r01, etc.) in subdirectories
                    foreach (string file in Directory.GetFiles(subDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext.Length >= 2 && ext[1] >= '0' && ext[1] <= '9')
                        {
                            Console.WriteLine($"{prefix}{new string('─', indent + 2)}[nested-rar] Found RAR part: {Path.GetFileName(file)}");
                            rarFiles.Add(file);
                        }
                    }
                }

                // Extract RAR files if any were found.
                if (rarFiles.Count > 0 && !string.IsNullOrEmpty(extractorPath))
                {
                    Console.WriteLine($"{prefix}{new string('─', indent)}[nested-rar] Extracting {rarFiles.Count} RAR file(s) via {Path.GetFileName(extractorPath)}");
                    string partsDir = Path.Combine(tempExtractDir, "_parts");
                    Directory.CreateDirectory(partsDir);

                    // Move all RAR files to partsDir
                    foreach (string rarFile in rarFiles)
                    {
                        string relativePath = Path.GetRelativePath(tempExtractDir, rarFile);
                        string destPath = Path.Combine(partsDir, relativePath);
                        string? parentDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parentDir) && parentDir != partsDir && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        // Make unique to avoid collisions
                        string uniqueDest = MakeUniquePath(destPath);
                        File.Move(rarFile, uniqueDest);
                    }

                    // Find and extract the first RAR volume
                    string[] rarParts = Directory.GetFiles(partsDir, "*.r*", SearchOption.AllDirectories);
                    if (rarParts.Length > 0)
                    {
                        string outputDir = _outputPath ?? Path.GetDirectoryName(zipPath) ?? ".";
                        string mainRar = FindFirstRarVolume(rarParts);
                        Console.WriteLine($"{prefix}{new string('─', indent + 2)}[nested-rar] Extracting from: {Path.GetFileName(mainRar)}");

                        // Run RAR extraction to a temp directory
                        string rarOutputDir = Path.Combine(tempExtractDir, "_rar_output");
                        Directory.CreateDirectory(rarOutputDir);

                        bool isSevenZip = Path.GetFileNameWithoutExtension(extractorPath).Equals("7z", StringComparison.OrdinalIgnoreCase);
                        string rarArgs = isSevenZip
                            ? $"x \"{mainRar}\" -o\"{rarOutputDir}\" -y"
                            : $"x -y \"{mainRar}\" \"{rarOutputDir}{Path.DirectorySeparatorChar}\"";

                        var psi = new ProcessStartInfo
                        {
                            FileName = extractorPath,
                            Arguments = rarArgs,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };

                        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Failed to start extractor process: {extractorPath}");

                        string stdout = process.StandardOutput.ReadToEnd();
                        string stderr = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            // Move extracted files to staging directory
                            foreach (string extractedFile in Directory.GetFiles(rarOutputDir, "*", SearchOption.AllDirectories))
                            {
                                string relativePath = _useSubfolder
                                    ? Path.GetRelativePath(rarOutputDir, extractedFile)
                                    : Path.GetFileName(extractedFile);
                                string destPath = Path.Combine(localStagingDir, relativePath);
                                destPath = MakeUniquePath(destPath);
                                string? parentDir = Path.GetDirectoryName(destPath);
                                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                                {
                                    Directory.CreateDirectory(parentDir);
                                }
                                File.Move(extractedFile, destPath);
                                Console.WriteLine($"{prefix}{new string('─', indent + 3)}→ {relativePath}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"{prefix}[WARN] RAR extraction failed: {stdout}\n{stderr}");
                        }

                        SafeDeleteDirectory(rarOutputDir);
                    }
                }

                SafeDeleteDirectory(tempExtractDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{prefix}[WARN] Failed to recursively extract '{Path.GetFileName(zipPath)}': {ex.Message}");
            }

            return collected;
        }
    }
}
