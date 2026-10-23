using System.Text.Json;
using System.Windows.Controls;

namespace EclatServerPanel;

public partial class AdminAuditView : UserControl
{
    public AdminAuditView() => InitializeComponent();
    internal void Apply(JsonElement data)
    {
        AuditRows.ItemsSource = PanelJson.Array(PanelJson.Get(data, "items")).Select(item => new AuditRow(
            $"{PanelJson.Date(PanelJson.Get(item, "at"))?.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {PanelJson.String(PanelJson.Get(item, "command"))}",
            AdminContract.MaskAccount(PanelJson.Text(PanelJson.Get(item, "target"), "Serveur")),
            "Motif : " + AdminContract.MaskAccount(PanelJson.Text(PanelJson.Get(item, "reason"), "—")),
            "Résultat : " + AdminContract.MaskAccount(PanelResponse.SafeError(PanelJson.Text(PanelJson.Get(item, "result")))))).ToList();
        var unavailable = PanelJson.Get(data, "serverAuditUnavailable");
        bool partial = unavailable.ValueKind == JsonValueKind.True || unavailable.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(unavailable.GetString());
        AuditState.Text = partial
            ? "Journal partiel : seules les modifications de configuration sont disponibles. Le journal des commandes du jeu n’a pas pu être lu."
            : "Dernières actions du serveur et modifications de configuration. Les empreintes de compte sont limitées à huit caractères.";
        AuditState.Foreground = (System.Windows.Media.Brush)FindResource(partial ? "Warning" : "Muted");
    }
    internal void Clear(string message = "Journal d’administration à charger.") { AuditRows.ItemsSource = Array.Empty<AuditRow>(); AuditState.Text = message; }
    private sealed record AuditRow(string Heading, string Target, string Reason, string Result);
}
