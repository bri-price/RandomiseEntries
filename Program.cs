using System.IO.Compression;

const string SummaryFileName = "_RandomiseEntries.txt";

bool addRandomNumber = false;
bool zipResults = false;
bool stripMetadata = true;
bool deleteAfterZip = false;
string? zipFileName = null;
string resultsFolder = "renamed";
string targetFolder = ".";
bool targetFolderSet = false;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];

    if (arg.StartsWith('/'))
    {
        switch (arg.ToUpperInvariant())
        {
            case "/D":
                deleteAfterZip = true;
                break;

            case "/N":
                addRandomNumber = true;
                break;

            case "/?":
                Console.WriteLine("""
                    Copies .jpg files from all subfolders of the target folder into a single results folder,
                    renaming each one to the part of its name between the first and last underscore,
                    e.g. 001_My_Photo_x.jpg becomes My_Photo.jpg.
                    Identifying metadata (author, keywords, rating, camera, GPS, etc.) is removed and the
                    file dates are reset, unless /S is given.

                    Usage: RandomiseEntries [/T folder] [/F name] [/N] [/Z [zipname] [/D]] [/S] [/?]

                      /T folder    Folder to search (all subfolders are searched). Default: current folder.
                      /F name      Results folder to create inside the target folder. Default: renamed
                      /N           Prefix each file with a random 4-digit number, e.g. 0427_My_Photo.jpg
                      /Z [zipname] Also zip the copied files into the results folder. The zip is named after
                                   the target folder unless a name is given.
                      /D           Delete the copied files once they have been zipped, leaving just the zip.
                                   Only allowed with /Z.
                      /S           Keep the original metadata and file dates instead of removing them.
                      /?           Show this help.

                    Options are not case sensitive and can be given in any order.
                    Folders containing a _RandomiseEntries.txt summary file (earlier results) are skipped.
                    """);
                return 0;

            // Metadata is removed by default; /S keeps it
            case "/S":
                stripMetadata = false;
                break;

            case "/Z":
                zipResults = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('/'))
                {
                    zipFileName = args[++i];
                }
                break;

            case "/F":
                if (i + 1 >= args.Length || args[i + 1].StartsWith('/'))
                {
                    Console.Error.WriteLine("Error: /F must be followed by a results folder name.");
                    return 1;
                }
                resultsFolder = args[++i];
                break;

            case "/T":
                if (i + 1 >= args.Length || args[i + 1].StartsWith('/'))
                {
                    Console.Error.WriteLine("Error: /T must be followed by a target folder.");
                    return 1;
                }
                if (targetFolderSet)
                {
                    Console.Error.WriteLine($"Error: Only one target folder may be specified (got '{targetFolder}' and '{args[i + 1]}').");
                    return 1;
                }
                targetFolder = args[++i];
                targetFolderSet = true;
                break;

            default:
                Console.Error.WriteLine($"Error: Unknown option '{arg}'.");
                return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"Error: Unexpected argument '{arg}'. Use /T to specify the target folder.");
        return 1;
    }
}

if (deleteAfterZip && !zipResults)
{
    Console.Error.WriteLine("Error: /D can only be used together with /Z.");
    return 1;
}

if (targetFolderSet && !Directory.Exists(targetFolder))
{
    Console.Error.WriteLine($"Error: Target folder '{targetFolder}' does not exist.");
    return 1;
}

string resultsPath = Path.Combine(targetFolder, resultsFolder);

if (!Directory.Exists(resultsPath))
{
    Directory.CreateDirectory(resultsPath);
}
else
{
    // The summary file on its own doesn't count, but it goes along with the rest if they're deleted
    string summaryFile = Path.Combine(resultsPath, SummaryFileName);
    string[] existingFiles = Directory.GetFiles(resultsPath)
        .Where(file => !Path.GetFileName(file).Equals(SummaryFileName, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (existingFiles.Length > 0)
    {
        Console.Write($"Results folder '{resultsPath}' already contains {existingFiles.Length} file(s). Delete them first? [y/N] ");
        string? answer = Console.ReadLine();
        if (answer?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (string file in existingFiles)
            {
                File.Delete(file);
            }
            File.Delete(summaryFile);
            Console.WriteLine($"Deleted {existingFiles.Length} file(s).");
        }
    }
}

string resultsFullPath = Path.GetFullPath(resultsPath).TrimEnd(Path.DirectorySeparatorChar);
int copied = 0;
int skipped = 0;
List<string> summaryLines = [];
List<string> copiedFiles = [];

Stack<string> foldersToScan = new(Directory.EnumerateDirectories(targetFolder));
while (foldersToScan.Count > 0)
{
    string folder = foldersToScan.Pop();

    // Don't pick up files already copied by this run or an earlier one (results folders hold a summary file)
    if (Path.GetFullPath(folder).Equals(resultsFullPath, StringComparison.OrdinalIgnoreCase) ||
        File.Exists(Path.Combine(folder, SummaryFileName)))
    {
        continue;
    }

    foreach (string subfolder in Directory.EnumerateDirectories(folder))
    {
        foldersToScan.Push(subfolder);
    }

    foreach (string sourceFile in Directory.EnumerateFiles(folder))
    {
        if (!Path.GetExtension(sourceFile).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        string originalName = Path.GetFileNameWithoutExtension(sourceFile);
        int firstUnderscore = originalName.IndexOf('_');
        int lastUnderscore = originalName.LastIndexOf('_');

        if (firstUnderscore < 0 || firstUnderscore == lastUnderscore)
        {
            Console.Error.WriteLine($"Skipping '{sourceFile}': name does not contain at least 2 underscores.");
            skipped++;
            continue;
        }

        string baseName = originalName.Substring(firstUnderscore + 1, lastUnderscore - firstUnderscore - 1);
        if (baseName.Length == 0)
        {
            Console.Error.WriteLine($"Skipping '{sourceFile}': nothing left between the first and last underscores.");
            skipped++;
            continue;
        }

        string extension = Path.GetExtension(sourceFile);
        string? destinationFile = GetDestinationFile(resultsPath, baseName, extension, addRandomNumber);
        if (destinationFile == null)
        {
            Console.Error.WriteLine($"Skipping '{sourceFile}': could not find an unused random number.");
            skipped++;
            continue;
        }

        if (stripMetadata)
        {
            byte[]? stripped = JpegMetadata.Strip(File.ReadAllBytes(sourceFile));
            if (stripped == null)
            {
                Console.Error.WriteLine($"Skipping '{sourceFile}': could not read it as a JPEG to remove its metadata.");
                skipped++;
                continue;
            }
            File.WriteAllBytes(destinationFile, stripped);

            // Set explicitly: Windows can give a re-created file the creation date of one recently deleted with the same name
            DateTime now = DateTime.Now;
            File.SetCreationTime(destinationFile, now);
            File.SetLastWriteTime(destinationFile, now);
            File.SetLastAccessTime(destinationFile, now);
        }
        else
        {
            File.Copy(sourceFile, destinationFile);
        }

        string line = $"{sourceFile} -> {Path.GetFileName(destinationFile)}";
        Console.WriteLine(line);
        summaryLines.Add(line);
        copiedFiles.Add(destinationFile);
        copied++;
    }
}

Console.WriteLine($"Copied {copied} file(s), skipped {skipped}.");

string? zipPath = null;
if (zipResults)
{
    // Default to the target folder's own name, e.g. "." run from C:\Pics gives Pics.zip
    string zipName = zipFileName ?? Path.GetFileName(Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar));
    if (zipName.Length == 0)
    {
        zipName = resultsFolder;
    }
    if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        zipName += ".zip";
    }

    zipPath = Path.Combine(resultsPath, zipName);
    File.Delete(zipPath);
    using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        foreach (string file in copiedFiles)
        {
            zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }
    }
    Console.WriteLine($"Zipped {copiedFiles.Count} file(s) into '{zipPath}'.");

    // The summary file stays so this folder is still recognised as results on later runs
    if (deleteAfterZip)
    {
        foreach (string file in copiedFiles)
        {
            File.Delete(file);
        }
        Console.WriteLine($"Deleted {copiedFiles.Count} copied file(s).");
    }
}

// Appended so the history survives runs where existing files were kept
string summaryPath = Path.Combine(resultsPath, SummaryFileName);
List<string> summary =
[
    $"=== Run at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
    $"Target folder:     {Path.GetFullPath(targetFolder)}",
    $"Add random number: {addRandomNumber}",
    $"Strip metadata:    {stripMetadata}",
    $"Delete after zip:  {deleteAfterZip}",
    $"Zip results:       {(zipPath != null ? Path.GetFileName(zipPath) : "No")}",
    $"Copied {copied} file(s), skipped {skipped}.",
    .. summaryLines,
    "",
];
File.AppendAllLines(summaryPath, summary);

return 0;

static string? GetDestinationFile(string resultsPath, string baseName, string extension, bool addRandomNumber)
{
    if (addRandomNumber)
    {
        // Give up eventually rather than looping forever if most numbers are taken
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            int number = Random.Shared.Next(1, 10000);
            string candidate = Path.Combine(resultsPath, $"{number:D4}_{baseName}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    string destination = Path.Combine(resultsPath, baseName + extension);
    for (int copy = 2; File.Exists(destination); copy++)
    {
        destination = Path.Combine(resultsPath, $"{baseName} ({copy}){extension}");
    }
    return destination;
}
