using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

public sealed record ConnectionSettings
{
    public string Host { get; init; } = "";
    public string User { get; init; } = "eclat-panel";
    public int Port { get; init; } = 22;
    public string KeyPath { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CanonicalHost => System.Net.IPAddress.TryParse(Host, out var address) ? address.ToString() : Host.ToLowerInvariant();
    [System.Text.Json.Serialization.JsonIgnore]
    public string EndpointIdentity => Port == 22 ? CanonicalHost : $"[{CanonicalHost}]:{Port}";
    public ConnectionSettings Snapshot() => this with { };
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EclatServerPanel");
    public static string SettingsPath => Path.Combine(DirectoryPath, "connection.json");
    public static string KnownHostsPath => Path.Combine(DirectoryPath, "known_hosts");
    public static string ProfilesPath => Path.Combine(DirectoryPath, "connections.json");
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => $"{User}@{Host}:{Port}";
    public static IReadOnlyList<ConnectionSettings> LoadProfiles() => LoadProfiles(ProfilesPath, Load());
    internal static IReadOnlyList<ConnectionSettings> LoadProfiles(string profilesPath, ConnectionSettings current)
    {
        var profiles = new List<ConnectionSettings>();
        try
        {
            if (File.Exists(profilesPath) && new FileInfo(profilesPath).Length <= 262144)
                profiles.AddRange(JsonSerializer.Deserialize<List<ConnectionSettings>>(File.ReadAllText(profilesPath)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (!string.IsNullOrWhiteSpace(current.Host) && !profiles.Any(p => p.EndpointIdentity == current.EndpointIdentity && p.User == current.User)) profiles.Add(current);
        return profiles.Where(p => !string.IsNullOrWhiteSpace(p.Host)).Take(100).ToArray();
    }
    public static ConnectionSettings Load() => Load(SettingsPath);
    internal static ConnectionSettings Load(string settingsPath)
    {
        try
        {
            if (new FileInfo(settingsPath).Length > 16384) return new();
            return JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(settingsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save() => Save(SettingsPath, ProfilesPath);
    internal void Save(string settingsPath, string profilesPath)
    {
        Validate();
        var previousProfiles = LoadProfiles(profilesPath, Load(settingsPath));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, settingsPath, true);
            var profiles = previousProfiles.Where(p => p.EndpointIdentity != EndpointIdentity || p.User != User).Append(this).TakeLast(100).ToArray();
            var profilesTemp = profilesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(profilesTemp, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(profilesTemp, profilesPath, true);
            }
            finally { if (File.Exists(profilesTemp)) File.Delete(profilesTemp); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Validate(bool requireKey = true)
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsWhiteSpace) || Host.Any(char.IsControl) || Host.Contains('%') || (!System.Net.IPAddress.TryParse(Host, out _) && !Regex.IsMatch(Host, @"\A(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\z"))) throw new InvalidOperationException("Renseignez une adresse IP ou un nom d’hôte valide, sans espace.");
        if (string.IsNullOrEmpty(User) || !Regex.IsMatch(User, @"\A[a-zA-Z_][a-zA-Z0-9_-]{0,63}\z")) throw new InvalidOperationException("Le nom du compte SSH est invalide.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Le port doit être compris entre 1 et 65535.");
        if (requireKey && (string.IsNullOrWhiteSpace(KeyPath) || !File.Exists(KeyPath))) throw new InvalidOperationException("Sélectionnez une clé SSH privée présente sur ce PC.");
    }
}
