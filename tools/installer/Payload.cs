// Built with the .NET Framework compiler included in Windows. The application itself is self-contained.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class Payload
{
    const string Prefix = "Mugonzei-Honyakuki/";
    const string Marker = ".mugonzei-install-v1";
    const string Manifest = ".mugonzei-files-v1";
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    static readonly string[] Required = { "SteamVrTranslator.exe", "SteamVrTranslator.dll", "SteamVrTranslator.runtimeconfig.json", "coreclr.dll",
        "tessdata/jpn.traineddata", "tessdata/chi_sim.traineddata", "tessdata/chi_tra.traineddata", "tessdata/eng.traineddata", "tessdata/kor.traineddata",
        "local-ocr-runtime/runtime-manifest.json", "local-ocr-runtime/local-japanese-ocr-worker.py", "local-ocr-runtime/python/python.exe", "THIRD-PARTY-NOTICES.md" };

    static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        try
        {
            if (args.Length == 2 && args[0] == "remove") Remove(Root(args[1]));
            else if (args.Length == 3 && args[0] == "verify") ReadZip(args[1], args[2], null);
            else if (args.Length == 4 && args[0] == "install") Install(Root(args[1]), args[2], args[3]);
            else throw new InvalidDataException("Invalid installer arguments.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    static bool SameOrBelow(string path, string parent)
    {
        return Paths.Equals(path, parent) || path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string Root(string supplied)
    {
        string root = Path.GetFullPath(supplied).TrimEnd('\\');
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamVrTranslator");
        if (!Regex.IsMatch(root, @"^[A-Za-z]:\\.+") || SameOrBelow(settings, root) || SameOrBelow(root, settings))
            throw new InvalidDataException("Choose a dedicated application directory outside your settings folder.");
        // Embedded Python has deep package paths. Extended paths also work when the
        // machine-wide Win32 long-path policy has not been enabled by an administrator.
        string extended = @"\\?\" + root;
        CheckLinks(extended);
        return extended;
    }

    static void CheckLinks(string path)
    {
        for (string p = path; p != null; p = Path.GetDirectoryName(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths cannot be installed or removed: " + p); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    static string Relative(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || Paths.Equals(name, Manifest)) throw new InvalidDataException("Invalid application file name.");
        foreach (string part in name.Split('/'))
            if (part.Length == 0 || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid application file name.");
        return name;
    }

    static string Child(string parent, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(parent, Relative(relative).Replace('/', '\\')));
        if (!SameOrBelow(path, parent) || Paths.Equals(path, parent)) throw new IOException("Invalid application path.");
        return path;
    }

    static void ReadZip(string path, string expected, string destination)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (var sha = SHA256.Create())
                if (!Paths.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""), expected)) throw new InvalidDataException("Embedded release SHA-256 verification failed.");
            input.Position = 0;
            var names = new HashSet<string>(Paths);
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release directory.");
                    string name = Relative(entry.FullName.Substring(Prefix.Length));
                    if (!names.Add(name)) throw new InvalidDataException("Duplicate release path.");
                    using (var stream = entry.Open())
                    {
                        if (destination == null) { stream.CopyTo(Stream.Null); continue; }
                        string target = Child(destination, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.CopyTo(output);
                        if (new FileInfo(target).Length != entry.Length) throw new InvalidDataException("Incomplete payload file: " + name);
                    }
                }
            }
            if (!Required.All(names.Contains)) throw new InvalidDataException("Application, .NET runtime, OCR or notices missing from the release.");
            if (destination != null) File.WriteAllLines(Path.Combine(destination, Manifest), names.OrderBy(s => s, Paths), new UTF8Encoding(false));
        }
    }

    static bool Owned(string root)
    {
        string file = Path.Combine(root, Marker);
        CheckLinks(file);
        return File.Exists(file) && Paths.Equals(File.ReadAllText(file), "Mugonzei Honyakuki\n" + root);
    }

    static HashSet<string> OwnedFiles(string app)
    {
        string file = Path.Combine(app, Manifest);
        CheckLinks(file);
        return new HashSet<string>(File.ReadAllLines(file).Select(Relative), Paths);
    }

    // Enumerate without following junctions, including links within an earlier installation.
    static IEnumerable<string> FilesIn(string directory)
    {
        CheckLinks(directory);
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Remove the linked item before continuing: " + path);
            if ((attributes & FileAttributes.Directory) == 0) yield return path;
            else foreach (string child in FilesIn(path)) yield return child;
        }
    }

    static void RemoveEmptyDirectories(string directory)
    {
        CheckLinks(directory);
        foreach (string sub in Directory.GetDirectories(directory)) RemoveEmptyDirectories(sub);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    static void RemoveWorkspace(string root, string work)
    {
        if (!Paths.Equals(Path.GetDirectoryName(work), root) || !Regex.IsMatch(Path.GetFileName(work), @"^\.mugonzei-(stage|old)-[0-9a-f]{32}$"))
            throw new InvalidOperationException("Invalid installer work directory.");
        if (!Directory.Exists(work)) return;
        foreach (string file in FilesIn(work).ToArray()) File.Delete(file);
        RemoveEmptyDirectories(work);
    }

    static void Install(string root, string zip, string digest)
    {
        bool upgrade = Owned(root);
        if (!upgrade && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Choose a new empty directory or the existing Mugonzei installation.");
        Directory.CreateDirectory(root);
        string app = Path.Combine(root, "app");
        CheckLinks(Path.Combine(root, "Uninstall.exe"));
        string stage = Path.Combine(root, ".mugonzei-stage-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, ".mugonzei-old-" + Guid.NewGuid().ToString("N"));
        CheckLinks(app);
        Directory.CreateDirectory(stage);
        bool movedOld = false, movedNew = false, committed = false;
        try
        {
            ReadZip(zip, digest, stage);
            if (upgrade)
            {
                RequireAppClosed(app);
                var owned = OwnedFiles(app);
                foreach (string file in FilesIn(app))
                {
                    string relative = file.Substring(app.Length + 1).Replace('\\', '/');
                    if (relative == Manifest || owned.Contains(relative)) continue;
                    // Carry user-created files forward. A collision aborts before the existing installation moves.
                    string preserve = Child(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(preserve));
                    File.Copy(file, preserve, false);
                }
                Directory.Move(app, old); movedOld = true;
            }
            Directory.Move(stage, app); movedNew = true;
            if (!upgrade) File.WriteAllText(Path.Combine(root, Marker), "Mugonzei Honyakuki\n" + root, new UTF8Encoding(false));
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                if (movedNew) Directory.Move(app, stage);
                if (movedOld) Directory.Move(old, app);
            }
            RemoveWorkspace(root, stage);
        }
        if (movedOld) RemoveWorkspace(root, old);
    }

    static void Remove(string root)
    {
        if (!Owned(root)) throw new IOException("The installation marker does not match this directory.");
        string app = Path.Combine(root, "app");
        RequireAppClosed(app);
        var owned = OwnedFiles(app);
        var files = FilesIn(app).ToArray();
        foreach (string file in files)
        {
            string relative = file.Substring(app.Length + 1).Replace('\\', '/');
            if (owned.Contains(relative) || relative == "SteamVrTranslator.vrmanifest") File.Delete(file);
        }
        File.Delete(Path.Combine(app, Manifest));
        RemoveEmptyDirectories(app);
        File.Delete(Path.Combine(root, Marker));
    }

    static void RequireAppClosed(string app)
    {
        string executable = Path.Combine(app, "SteamVrTranslator.exe").Substring(4);
        foreach (var process in Process.GetProcessesByName("SteamVrTranslator"))
        {
            using (process)
            {
                try
                {
                    if (Paths.Equals(process.MainModule.FileName, executable))
                        throw new IOException("Close Mugonzei Honyakuki before upgrading or uninstalling it.");
                }
                catch (Win32Exception) { } // Other users' processes may be inaccessible.
                catch (InvalidOperationException) { } // Exited during enumeration.
            }
        }
    }
}
