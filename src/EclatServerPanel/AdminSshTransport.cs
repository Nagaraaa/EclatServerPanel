using System.Text.Json;

namespace EclatServerPanel;

public sealed partial class SshTransport
{
    public async Task<JsonElement> SendAdminAsync(ConnectionSettings settings, string action, JsonElement fields)
    {
        settings = settings.Snapshot(); settings.Validate();
        string request = AdminContract.Request(action, fields);
        if (!KnownHostsTrust.HasTrustedHost(settings)) throw new InvalidOperationException("Vérifiez d’abord l’empreinte de cette adresse et de ce port SSH.");
        var result = await ProcessRunner.RunAsync("ssh.exe", BuildArguments(settings, ConnectionSettings.KnownHostsPath), request + "\n", TimeSpan.FromSeconds(action is "config.save" or "config.restore" ? 75 : 50), 2 * 1024 * 1024).ConfigureAwait(false);
        if (result.Code != 0) throw new InvalidOperationException(ParseRemoteError(result.Text) ?? PanelResponse.SafeError(string.IsNullOrWhiteSpace(result.Error) ? "La commande n’a pas été confirmée. Le serveur et l’outil Linux doivent prendre en charge cette fonction." : result.Error));
        return AdminContract.ParseResponse(result.Text);
    }
}
