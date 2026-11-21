# RARZipProcessor - Re-zip Mode Change

## Current Behavior (BROKEN/NEEDS CHANGE)
When using `--rezip`, all extracted folders are merged into **ONE** output ZIP file per folder.
- Extract all ZIPs → individual extraction folders
- Collect ALL files from ALL extraction folders → ONE staging directory
- Create ONE merged ZIP from that staging directory

## Desired Behavior
When using `--rezip`, each extracted folder should become its **OWN** output ZIP file.
- Extract all ZIPs → individual extraction folders
- For EACH extraction folder:
  - Create its own output ZIP (named after the original source ZIP)
  - Handle nested ZIPs and RAR volumes per-ZIP

## Current Code Analysis

### File: `C:/Users/jeff/source/repos/RARZipProcessor/Program.cs` (1574 lines)

### Key data structures:
- `extractedZipFolders`: List of extracted folder paths (created in extraction loop, line ~705)
- `partsDir`: temporary directory for RAR volume collection
- `nfoCandidates`: list of NFO files to deduplicate

### Extraction loop (lines ~713-730):
```csharp
foreach (string zipPath in zipFiles.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
{
    string baseName = Path.GetFileNameWithoutExtension(zipPath);
    string zipExtractDir = Path.Combine(folder, baseName);
    // collision handling: suffix numbering
    
    ZipFile.ExtractToDirectory(zipPath, zipExtractDir);
    extractedZipFolders.Add(zipExtractDir);
}
```

**MISSING**: No tracking of which original ZIP each extracted folder came from.
The mapping is implicit (baseName matches zip filename), but broken by collision handling (suffix numbering).

### RAR handling step (lines ~735-830):
- Move RAR volumes from extracted folders → `partsDir`
- Track `rarSourceMapping` (which RAR came from which extraction folder)
- Extract RARs using 7z/unrar → `outputDir`
- Re-place extracted files back to source subfolders (if --output is null)

### Re-zip block (lines ~899-1043):
```csharp
if (_reZip)
{
    string? rezipOutputDir = _outputPath ?? folder;
    // ONE staging dir for ALL folders
    string stagingDir = Path.Combine(folder, "_rzp_staging_" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(stagingDir);
        // Loop through ALL extracted folders
        foreach (string zipExtractDir in extractedZipFolders)
        {
            // Move files from EACH folder into the SHARED staging dir
            // Handle nested ZIPs, RAR volumes, regular files
        }
        // Also process output directory if --output was set
        if (_outputPath != null) { ... }
        // ONE merged ZIP from the staging dir
        CreateZipFromFolder(stagingDir, zipPath);
    }
    finally
    {
        SafeDeleteDirectory(stagingDir);
    }
}
else
{
    // Delete extracted folders
}
```

## Required Changes

### 1. Track (originalZip, extractedFolder) pairs
Change the data structure to track pairs:
```csharp
// Change from:
var extractedZipFolders = new List<string>();
// To:
var zipPairs = new List<(string originalZip, string extractedDir)>();
// Or keep parallel lists:
var zipPaths = new List<string>();
```

### 2. Replace re-zip block with per-ZIP logic
Instead of:
```
one staging dir → loop all folders → collect → one merged ZIP
```

Should be:
```
for each (originalZip, extractedFolder):
    create per-folder staging dir
    loop files in THIS folder only:
        handle nested ZIPs
        handle RAR volumes  
        move regular files to staging
    create output ZIP for THIS folder
    clean up per-folder staging dir
    delete extracted folder
```

### 3. Handle --output and --subfolder flags
- --output: Output ZIP goes to specified directory (instead of source folder)
- --subfolder: Preserve file paths within the ZIP

## Plan

### Step 1: Track original ZIP paths
**Location**: Lines 705-730 (extraction loop)
**Change**: Add tracking of (originalZip, extractedFolder) pairs
- Add `var zipPaths = new List<string>();` before extraction loop
- Inside loop: `zipPaths.Add(zipPath);` and `zipPairs.Add((zipPath, zipExtractDir));`

### Step 2: Replace re-zip block
**Location**: Lines 899-1043 (the `if (_reZip)` block)
**Change**: Replace "merge all into one" with "create one per folder"

For each (originalZip, extractedFolder) pair:
1. Determine output path: `{--output or source folder}/{originalZip base name}.zip`
2. Create temp staging dir for this folder only
3. Process files from THIS extracted folder:
   - Skip metadata (file_id.diz, disk.id)
   - Handle nested .zip → recursive extraction
   - Handle RAR volumes → extract to temp, collect files
   - Move regular files to staging dir (respecting --subfolder)
4. Create output ZIP from staging dir
5. Log creation of output ZIP
6. Delete extracted folder
7. Clean up temp staging dir

### Step 3: Handle RAR extraction per-ZIP
**INTENTIONALLY SKIPPED**. RAR extraction happens before the re-zip step via partsDir.
By the time we reach the re-zip block, RAR volumes have already been extracted.
No per-ZIP RAR extraction is needed.

### Step 4: Update --output handling
**INTENTIONALLY CHANGED**. When --output is set with --rezip, files from outputDir are NOT included.
This makes more sense in the per-ZIP model: outputDir files don't belong to any specific ZIP.
The --output flag specifies where the per-ZIP output files go, not what files to include.

### Step 5: Update the else block
The `else` block (non-rezip mode) should remain unchanged — it just deletes extracted folders.

### Step 6: Update preview/dry-run output
Update the `PreviewDryRun` method to reflect per-ZIP output instead of merged output.

## Changes Made (Step 1 & 2 of 6)

### Step 1: ✅ Track original ZIP paths
- Added `var zipPaths = new List<string>();` before extraction loop (line ~706)
- Added `zipPaths.Add(zipPath);` inside extraction loop (line ~732)

### Step 2: ✅ Replaced re-zip block
- Original (lines 901-1043): ONE staging dir → loop ALL folders → ONE merged ZIP
- New: For each (originalZip, extractedFolder) pair:
  - Create per-ZIP staging dir
  - Loop files in THIS folder only
  - Handle nested ZIPs (recursive extraction via ExtractZipRecursive)
  - Move regular files to staging
  - Create output ZIP for THIS folder
  - Delete extracted folder
  - Clean up staging dir

## Testing Checklist
- [x] Track original ZIP paths alongside extracted folders
- [x] Replace re-zip block with per-ZIP logic
- [x] Code compiles without errors or warnings
- [ ] Test with multiple ZIPs in one folder → should create one ZIP per source ZIP
- [ ] Test with nested ZIPs inside ZIPs → should recursively extract and package
- [ ] Test with --subfolder flag → should preserve directory structure
- [ ] Test with --output flag → should output to specified directory
- [ ] Test with --dry-run flag → should show per-ZIP preview
- [ ] Test collision handling (duplicate folder names) → should work correctly
- [ ] Test with .skip sentinel files → should skip correctly

## Notes
- RAR extraction logic is UNCHANGED (still global: partsDir → outputDir)
- --output flag: Output ZIP goes to specified directory (instead of source folder)
- --subfolder flag: Preserves directory structure within each output ZIP
- Metadata files (file_id.diz, disk.id) are still filtered out
- Collision handling: If output ZIP already exists, adds _1, _2, etc.
- Preview dry-run (PreviewFolder) still works the same - it's just a preview

## Risk Areas
1. **Cross-contamination between folders**: The new per-ZIP model should prevent this, but need to verify staging dirs don't mix.
2. **Nested ZIP/RAR extraction**: Need to ensure per-ZIP RAR extraction works correctly.
3. **--output flag**: The behavior changes significantly. Need to decide if --output with --rezip makes sense.
4. **--subfolder flag**: Need to verify subfolder paths are preserved correctly in each output ZIP.
5. **Performance**: Creating many small ZIPs vs one large ZIP may have different performance characteristics.

## Notes
- The RAR extraction uses 7z.exe or unrar.exe (auto-detected from common paths)
- Multi-part RAR sets are handled by the extractor (7z/unrar handles .r00/.r01/.part1.rar automatically)
- NFO files are deduplicated (keep one, discard rest)
- Metadata files (file_id.diz, disk.id) are filtered out in re-zip mode
