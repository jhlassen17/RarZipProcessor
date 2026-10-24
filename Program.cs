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
                }
                else
                {
                    nonFlagArgs.Add(arg);
                }
            }

            // Handle --output <path> form (two separate args)
            if (string.IsNullOrEmpty(outputDir) && nonFlagArgs.Count >= 2)
            {
                for (int i = 1; i < nonFlagArgs.Count; i++)
                {
                    if (nonFlagArgs[i].Equals("--output", StringComparison.OrdinalIgnoreCase) ||
                        nonFlagArgs[i].Equals("--output=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < nonFlagArgs.Count)
                        {
                            outputDir = nonFlagArgs[i + 1];
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
            Console.WriteLine($"Found {foldersWithZips.Count} folder(s) with ZIP files.");
            Console.WriteLine();

            if (foldersWithZips.Count == 0)
            {
                Console.WriteLine("No folders with ZIP files found. Nothing to do.");
                return;
            }

            // Shuffle and pick first 5.
            var random = new Random();
            var shuffled = foldersWithZips.OrderBy(_ => random.NextDouble()).ToList();
            int previewCount = Math.Min(5, shuffled.Count);
            var previewFolders = shuffled.Take(previewCount).ToList();

            Console.WriteLine($"Previewing {previewCount} folder(s) (of {foldersWithZips.Count} total).");
            Console.WriteLine($"Re-zip mode: {_reZip}");
            Console.WriteLine();

            foreach (string folder in previewFolders)
            {
                PreviewFolder(folder);
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

            string[] zipFiles = Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly);
            if (zipFiles.Length > 0)
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

            // Collect all files from all ZIPs.
            var allEntries = new List<(string zipName, string entryPath)>();
            foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Length > 0) // Skip empty directories.
                        {
                            allEntries.Add((Path.GetFileName(zipPath), entry.FullName));
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
            foreach (var (zipName, entryPath) in allEntries.OrderBy(e => e.entryPath, StringComparer.OrdinalIgnoreCase))
            {
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
                Console.WriteLine($"      (from {zipFiles.Length} ZIP file(s) in {folderName}/)");
                foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase).Take(5))
                {
                    Console.WriteLine($"        {Path.GetFileName(zipPath)}");
                }
                if (zipFiles.Length > 5)
                {
                    Console.WriteLine($"        ... and {(zipFiles.Length - 5)} more");
                }
            }

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
                Console.WriteLine($"  [ERROR] Failed processing '{dir}': {ex.Message}");
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
                if (rarParts.Length == 0)
                {
                    Console.WriteLine("  [WARN] No rar parts found after extracting zips. Skipping extraction step.");
                }
                else
                {
                    string mainRar = FindFirstRarVolume(rarParts);
                    Console.WriteLine($"  Extracting rar set starting at: {Path.GetFileName(mainRar)}");
                    RunRarExtraction(extractorPath, mainRar, outputDir);
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
                    Console.WriteLine($"  Re-zipping all subfolders -> {Path.GetFileName(zipPath)}");

                    // Collect all files from all subfolders into a temporary staging folder.
                    string stagingDir = Path.Combine(folder, "_rzp_staging_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(stagingDir);
                        foreach (string zipExtractDir in extractedZipFolders)
                        {
                            // Move files from each subfolder into staging, preserving structure when --subfolder.
                            foreach (string file in Directory.GetFiles(zipExtractDir, "*", SearchOption.AllDirectories))
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
                            // Delete the individual subfolder.
                            SafeDeleteDirectory(zipExtractDir);
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
    }
}
