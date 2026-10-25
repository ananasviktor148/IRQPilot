using System.IO;
using System.Text.Json;
using CorePilot.Models;

namespace CorePilot.Services;

/// <summary>JSON persistence for device backups and named profiles.</summary>
public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CorePilot");

    private string BackupDir => Path.Combine(DataRoot, "Backups");
    private string ProfileDir => Path.Combine(DataRoot, "Profiles");

    private static string SafeFileName(string instanceId) =>
        string.Concat(instanceId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private string BackupPath(string instanceId) => Path.Combine(BackupDir, SafeFileName(instanceId) + ".json");

    // ---- Backups ----

    public void SaveBackup(DeviceBackup backup)
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(BackupPath(backup.InstanceId), JsonSerializer.Serialize(backup, JsonOptions));
    }

    public DeviceBackup? LoadBackup(string instanceId)
    {
        var path = BackupPath(instanceId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeviceBackup>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<DeviceBackup> LoadAllBackups()
    {
        if (!Directory.Exists(BackupDir)) return Array.Empty<DeviceBackup>();
        var result = new List<DeviceBackup>();
        foreach (var file in Directory.EnumerateFiles(BackupDir, "*.json"))
        {
            try
            {
                var backup = JsonSerializer.Deserialize<DeviceBackup>(File.ReadAllText(file), JsonOptions);
                if (backup is not null) result.Add(backup);
            }
            catch (JsonException) { /* skip unreadable backup */ }
        }
        return result;
    }

    // ---- Profiles ----

    public void SaveProfile(CorePilotProfile profile)
    {
        Directory.CreateDirectory(ProfileDir);
        var path = Path.Combine(ProfileDir, SafeFileName(profile.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public CorePilotProfile? LoadProfile(string name)
    {
        var path = Path.Combine(ProfileDir, SafeFileName(name) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CorePilotProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(ProfileDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(ProfileDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void DeleteProfile(string name)
    {
        var path = Path.Combine(ProfileDir, SafeFileName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
