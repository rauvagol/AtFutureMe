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
    }

    public AppDataFile Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0)
                return new AppDataFile();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppDataFile>(json, JsonOptions) ?? new AppDataFile();
        }
        catch
        {
            return new AppDataFile();
        }
    }

    public void Save(AppDataFile data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, FilePath, true);
        File.Delete(tmp);
    }
}
