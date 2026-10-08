using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZRevive.Launcher.Install;

/// <summary>
/// Written into the ZRevive folder itself (not just Settings) so a moved or copied install
/// still knows which release it is, and so ValidatePair can tell "our folder" from
/// "somebody's random folder".
/// </summary>
public sealed class InstallState
{
    public const string MarkerFile = ".zrevive-install.json";

    [JsonPropertyName("sourceFolder")] public string? SourceFolder { get; set; }
    [JsonPropertyName("releaseHash")] public string? ReleaseHash { get; set; }
    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; }
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("usedHardlinks")] public bool UsedHardlinks { get; set; }
    [JsonPropertyName("baseBuiltUtc")] public DateTime? BaseBuiltUtc { get; set; }
    [JsonPropertyName("deltaAppliedUtc")] public DateTime? DeltaAppliedUtc { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }

    public static string PathFor(string installFolder) => Path.Combine(installFolder, MarkerFile);

    public static InstallState Load(string installFolder)
    {
        try
        {
            var p = PathFor(installFolder);
            if (File.Exists(p)) return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(p)) ?? new InstallState();
        }
        catch { }
        return new InstallState();
    }

    public void Save(string installFolder)
    {
        Directory.CreateDirectory(installFolder);
        var p = PathFor(installFolder);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, p, overwrite: true);
    }
}
