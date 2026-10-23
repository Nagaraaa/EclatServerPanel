using System.IO;
using System.Text.Json;

namespace EclatServerPanel;

internal static partial class SelfTests
{
    static partial void RunAdditional() => VerifyDownloadFilesAsync().GetAwaiter().GetResult();

    private static async Task VerifyDownloadFilesAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eclat-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var demo = new DemoTransport();
            var settings = new ConnectionSettings();
            var snapshot = await demo.SendAsync(settings, "backup").ConfigureAwait(false);
            var download = await demo.SendAsync(settings, "download", snapshot.GetProperty("name").GetString()).ConfigureAwait(false);
            string destination = Path.Combine(directory, "copie.sqlite");
            await PanelFiles.SaveDownloadAsync(download, destination).ConfigureAwait(false);
            byte[] expected = SshTransport.VerifyDownload(download);
            Assert((await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(expected), "Verified download was not saved intact");
            Assert(Directory.GetFiles(directory).Length == 1, "Successful download left a temporary file");

            byte[] original = "Existing destination must remain intact"u8.ToArray();
            await File.WriteAllBytesAsync(destination, original).ConfigureAwait(false);
            var invalid = JsonSerializer.SerializeToElement(new { base64 = download.GetProperty("base64").GetString(), sha256 = new string('0', 64) });
            bool rejected = false;
            try { await PanelFiles.SaveDownloadAsync(invalid, destination).ConfigureAwait(false); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Invalid checksum was saved to the destination");
            Assert((await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(original), "Invalid checksum overwrote an existing destination");
            Assert(Directory.GetFiles(directory).Length == 1, "Rejected download left a temporary file");

            if (OperatingSystem.IsWindows())
            {
                using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool lockedRejected = false;
                    try { await PanelFiles.SaveDownloadAsync(download, destination).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lockedRejected = true; }
                    Assert(lockedRejected, "Locked destination was unexpectedly replaced");
                }
                Assert((await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(original), "Failed replacement damaged the destination");
                Assert(Directory.GetFiles(directory).Length == 1, "Failed replacement left a temporary file");
            }

            foreach (string invalidDestination in new[] { "", "relative.sqlite", directory, Path.Combine(directory, "missing", "copy.sqlite") })
            {
                rejected = false;
                try { await PanelFiles.SaveDownloadAsync(download, invalidDestination).ConfigureAwait(false); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Invalid destination was accepted");
            }
            Assert(Directory.GetFiles(directory).Length == 1, "Invalid destination left a temporary file");
        }
        finally { Directory.Delete(directory, true); }
    }
}
