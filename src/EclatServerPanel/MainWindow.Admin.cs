using System.Text.Json;
using System.Windows;

namespace EclatServerPanel;

public partial class MainWindow
{
    private static readonly JsonElement EmptyAdminFields = JsonSerializer.SerializeToElement(new { });
    private void InitializeAdministration()
    {
        CommandsPage.ReadRequested = LoadAdminReadAsync;
        CommandsPage.ExecuteRequested = ExecuteAdminAsync;
        CommandsPage.ConfigRequested = WriteConfigurationAsync;
        CommandsPage.Failed = message => Toast(message, ToastKind.Error);
    }
    private async void LogsTab_Changed(object sender, RoutedEventArgs e)
    {
        if (ServiceLogsBody is null || AdminJournal is null || pages is null) return;
        bool administration = AdminLogTab.IsChecked == true;
        ServiceLogsBody.Visibility = administration ? Visibility.Collapsed : Visibility.Visible;
        AdminJournal.Visibility = administration ? Visibility.Visible : Visibility.Collapsed;
        if (currentPage == "Logs" && !suppressNavigationLoad)
        {
            if (administration) await LoadAdminAuditAsync();
            else await LoadLogsAsync();
        }
    }
    internal Task LoadAdministrationAsync() => RunAsync("Lecture de l’administration…", async () =>
    {
        try
        {
            var data = await transport.SendAdminAsync(ActiveSettings, "admin.overview", EmptyAdminFields);
            if (closed) return;
            CommandsPage.ApplyOverview(data, ActiveSettings);
            RenderStatus(await transport.SendAsync(ActiveSettings, "status"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CommandsPage.InvalidateRead("overview"); CommandsPage.SetError(ex.Message); throw; }
    });
    private Task LoadAdminReadAsync(string action) => RunAsync("Lecture des données d’administration…", async () =>
    {
        try
        {
            if (action == "config")
            {
                var config = await transport.SendAdminAsync(ActiveSettings, "config.get", EmptyAdminFields);
                CommandsPage.ApplyConfiguration(config, ActiveSettings);
                CommandsPage.ApplyHistory(await transport.SendAdminAsync(ActiveSettings, "config.history", EmptyAdminFields));
            }
            else if (action == "statistics") CommandsPage.ApplyStatistics(await transport.SendAdminAsync(ActiveSettings, "statistics", EmptyAdminFields));
            else throw new InvalidOperationException("Lecture d’administration inconnue.");
        }
        catch { CommandsPage.InvalidateRead(action); throw; }
    });
    internal Task LoadAdminAuditAsync() => RunAsync("Lecture du journal d’administration…", async () =>
    {
        try { AdminJournal.Apply(await transport.SendAdminAsync(ActiveSettings, "admin.audit", EmptyAdminFields)); }
        catch { AdminJournal.Clear("Le journal n’a pas pu être chargé. Un serveur et un outil Linux compatibles sont nécessaires."); throw; }
    });
    internal Task ExecuteAdminAsync(PlannedAdminCommand plan, ConnectionSettings? origin) => RunAsync("Préparation de la commande…", async () =>
    {
        if (origin != ActiveSettings) throw new InvalidOperationException("La connexion a changé. Actualisez les cibles d’administration.");
        AdminContract.ValidateCommand(plan.Command, plan.Arguments);
        if (plan.NeedsConfirmation && !await ConfirmAsync(plan.Title, plan.Message, "Confirmer", danger: true, requiredText: plan.Confirmation) || closed) return;
        ActivityText.Text = "Commande en cours…";
        bool sent = false;
        try
        {
            sent = true;
            var data = await transport.SendAdminAsync(ActiveSettings, "admin.command", AdminContract.CommandFields(plan));
            Toast(plan.Command == "casino.refund" ? "Jetons ajoutés à la file de réclamation du compte." : "Commande confirmée par le serveur.");
            CommandsPage.SetError("");
        }
        finally { if (sent) await RefreshAdministrationAfterActionAsync(); }
    });
    private async Task RefreshAdministrationAfterActionAsync()
    {
        try
        {
            CommandsPage.ApplyOverview(await transport.SendAdminAsync(ActiveSettings, "admin.overview", EmptyAdminFields), ActiveSettings);
            RenderStatus(await transport.SendAsync(ActiveSettings, "status"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CommandsPage.SetError("Le résultat actuel n’a pas pu être relu. Ne répétez pas une commande dont l’issue est incertaine.");
            MarkStatusUnknown("Le contrôle après commande est indisponible. Actualisez avant une nouvelle action.");
            Toast("Contrôle après commande : " + ex.Message, ToastKind.Info);
        }
    }
    private Task WriteConfigurationAsync(string action, JsonElement fields, ConnectionSettings? origin) => RunAsync("Modification de la configuration…", async () =>
    {
        if (origin != ActiveSettings) throw new InvalidOperationException("La connexion a changé. Relisez la configuration du serveur.");
        AdminContract.Request(action, fields);
        if (action == "config.restore" && !await ConfirmAsync("Restaurer cette configuration ?", "Les évènements, bannières et prix seront remplacés par la version sélectionnée. La configuration actuelle sera conservée dans l’historique.", "Restaurer", danger: true) || closed) return;
        var result = await transport.SendAdminAsync(ActiveSettings, action, fields);
        CommandsPage.ConfigurationResult(result);
        bool accepted = PanelJson.Get(result, "accepted").ValueKind == JsonValueKind.True;
        Toast(accepted ? "Configuration chargée et acceptée par le jeu." : "Configuration écrite ; son chargement reste à contrôler.", accepted ? ToastKind.Success : ToastKind.Info);
        try
        {
            CommandsPage.ApplyConfiguration(await transport.SendAdminAsync(ActiveSettings, "config.get", EmptyAdminFields), ActiveSettings);
            CommandsPage.ApplyHistory(await transport.SendAdminAsync(ActiveSettings, "config.history", EmptyAdminFields));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CommandsPage.SetError("Écriture confirmée, relecture indisponible : " + ex.Message); }
    });
    internal async Task CaptureAdministrationAsync(string mode)
    {
        await ShowPageForCaptureAsync(mode.StartsWith("map", StringComparison.Ordinal) ? "Map" : mode == "audit" ? "Logs" : "Commands");
        if (mode == "audit") { AdminLogTab.IsChecked = true; await WaitForIdleAsync(); }
        else if (mode.StartsWith("map", StringComparison.Ordinal))
        {
            MapPage.CloseOverlay();
            MapPage.CaptureMode(mode == "map-cards" ? "cards" : mode == "map-places" ? "places" : "table", mode == "map-fan" ? 5 : mode == "map-crowd" ? 12 : 0);
        }
        else { CommandsPage.CaptureTab(mode == "config" ? "config" : mode == "statistics" ? "stats" : "commands"); await WaitForIdleAsync(); }
        await Task.Delay(1100);
    }
    internal async Task ShowAdminConfirmationForCaptureAsync()
    {
        _ = ConfirmAsync("Modifier le solde Éclats ?", "Crédit de 100 Éclats au compte de démonstration. Le motif et le résultat seront inscrits dans le journal.", "Confirmer", danger: true, requiredText: "100");
        await Task.Delay(500);
    }
    internal void DismissCaptureDialog() => CloseDialog(false);
}
