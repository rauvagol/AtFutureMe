using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AtFutureMe.Models;

namespace AtFutureMe.Services;

public sealed class DataStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string FilePath { get; }
    public string BackupPath => FilePath + ".bak";
    public string Backup2Path => FilePath + ".bak2";

    public DataStore()
    {
        FilePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "atfutureme.json");
        try
        {
            var probe = FilePath + ".write-test";
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AtFutureMe");
            Directory.CreateDirectory(fallback);
            FilePath = Path.Combine(fallback, "atfutureme.json");
        }

        // auto model
        App.Log("Data file: " + FilePath);
    }

    public AppDataFile Load()
    {
        // auto model: try main file, then rotating backups — never silently invent empty over a recoverable file
        foreach (var path in new[] { FilePath, BackupPath, Backup2Path })
        {
            var loaded = TryLoadPath(path);
            if (loaded != null)
            {
                if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                    App.Log($"Loaded data from backup: {path}");
                return loaded;
            }
        }

        App.Log("No usable data file; starting empty");
        return new AppDataFile();
    }

    // auto model
    static AppDataFile? TryLoadPath(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                App.Log($"Skipping empty file: {path}");
                return null;
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<AppDataFile>(json, JsonOptions);
            if (data == null)
            {
                App.Log($"Deserialize returned null: {path}");
                return null;
            }

            if (IsEmpty(data) && info.Length > 2048)
            {
                App.Log($"File parses as empty but is {info.Length} bytes; treating as corrupt: {path}");
                return null;
            }

            return data;
        }
        catch (Exception ex)
        {
            App.Log($"Load failed ({path}): {ex.Message}");
            return null;
        }
    }

    public void Save(AppDataFile data)
    {
        try
        {
            // auto model: never overwrite a non-empty on-disk file with an empty dataset
            if (IsEmpty(data) && File.Exists(FilePath))
            {
                var existingLen = new FileInfo(FilePath).Length;
                if (existingLen > 2048)
                {
                    var existing = TryLoadPath(FilePath);
                    if (existing != null && !IsEmpty(existing))
                    {
                        App.Log($"Refusing to overwrite {existingLen}-byte data file with empty dataset");
                        return;
                    }
                }
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
            {
                // rotate .bak -> .bak2, then replace main with backup slot
                try
                {
                    if (File.Exists(BackupPath))
                        File.Copy(BackupPath, Backup2Path, true);
                }
                catch (Exception ex)
                {
                    App.Log("Backup rotate failed: " + ex.Message);
                }

                File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, FilePath);
            }

            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch (Exception ex)
        {
            App.Log("Save failed: " + ex);
            try
            {
                var tmp = FilePath + ".tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // ignore cleanup
            }

            throw;
        }
    }

    /// <summary>Write current data, then copy the data file to a timestamped sibling that won't be rotated away.</summary>
    // auto model
    public string CreateManualBackup(AppDataFile data)
    {
        Save(data);
        if (!File.Exists(FilePath))
            throw new InvalidOperationException("Data file was not written.");

        var dir = Path.GetDirectoryName(FilePath) ?? ".";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = Path.GetFileNameWithoutExtension(FilePath);
        var dest = Path.Combine(dir, $"{name}.manual-{stamp}.json");
        File.Copy(FilePath, dest, overwrite: false);
        return dest;
    }

    // auto model
    public static bool IsEmpty(AppDataFile data) =>
        data.Categories.Count == 0
        && data.Once.Count == 0
        && data.Todos.Count == 0
        && data.Events.Count == 0
        && data.Notes.Count == 0
        && data.AppLayouts.Count == 0;
}
