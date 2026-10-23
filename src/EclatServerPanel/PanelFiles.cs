using System.IO;
using System.Text.Json;

namespace EclatServerPanel;

internal static class PanelFiles
{
    internal static async Task SaveDownloadAsync(JsonElement data, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Choisissez un chemin de fichier complet pour la sauvegarde.");

        string path;
        try { path = Path.GetFullPath(destination); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("Le chemin de destination de la sauvegarde est invalide.");
        }
        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName != fileName.TrimEnd(' ', '.') || Directory.Exists(path))
            throw new InvalidOperationException("La destination doit désigner un fichier de sauvegarde valide.");
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Le dossier de destination n’existe pas.");

        // A large base64 response and its checksum must never block the WPF Dispatcher.
        byte[] bytes = await Task.Run(() => SshTransport.VerifyDownload(data)).ConfigureAwait(false);
        string temporary = Path.Combine(directory, "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            // The staging file shares the destination's volume; no partial database is exposed.
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
