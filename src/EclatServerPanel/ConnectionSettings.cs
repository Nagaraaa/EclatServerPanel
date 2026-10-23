using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EclatServerPanel;

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "";
    public string User { get; set; } = "eclat-panel";
    public int Port { get; set; } = 22;
    public string KeyPath { get; set; } = "";
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EclatServerPanel");
    public static string SettingsPath => Path.Combine(DirectoryPath, "connection.json");
    public static string KnownHostsPath => Path.Combine(DirectoryPath, "known_hosts");
    public static ConnectionSettings Load()
    {
        try { return JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        Validate();
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
    public void Validate(bool requireKey = true)
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || (!System.Net.IPAddress.TryParse(Host, out _) && !Regex.IsMatch(Host, @"^[a-zA-Z0-9][a-zA-Z0-9.-]*$"))) throw new InvalidOperationException("Renseignez une adresse IP ou un nom d’hôte valide, sans espace.");
        if (!Regex.IsMatch(User, @"^[a-zA-Z_][a-zA-Z0-9_-]{0,63}$")) throw new InvalidOperationException("Le nom du compte SSH est invalide.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Le port doit être compris entre 1 et 65535.");
        if (requireKey && (string.IsNullOrWhiteSpace(KeyPath) || !File.Exists(KeyPath))) throw new InvalidOperationException("Sélectionnez une clé SSH privée présente sur ce PC.");
    }
}
