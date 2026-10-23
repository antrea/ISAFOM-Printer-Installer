using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Isafom.Core;

namespace Isafom.Windows;

public sealed record SelectiveQueue(string QueueId, string Name, string DriverName, string PortName,
    string Address, string Status, string Association, bool Associated, string Protection, bool CanSelect,
    string Signature, bool IsDefault = false, string[]? Reasons = null)
{
    public string SelectionReasons => string.Join("; ", Reasons ?? []);
}
public sealed record SelectiveRemovalPlan(SelectiveQueue[] Queues, RemovalPort[] Ports, RemovalDriver[] Drivers,
    RemovalPackage[] Packages, string[] Retained, string Fingerprint, RemovalDriverFile[]? Files=null, string[]? Unresolved=null)
{
    public bool RemovesDefaultPrinter => Queues.Any(q => q.IsDefault);
    public bool RemovesUnverifiedQueues => Queues.Any(q => !q.Associated);
    public string Description => "Code selezionate (" + Queues.Length + "):\n\n" +
        string.Join("\n\n", Queues.Select(q => $"• {q.Name}\n  ID Windows: {q.QueueId}\n  Driver: {q.DriverName}\n  Porta: {q.PortName} — {q.Address}\n  Stato: {q.Status}\n  {q.Association}\n  Motivi: {q.SelectionReasons}\n  Predefinita Windows: {(q.IsDefault ? "sì" : "no")}")) +
        (RemovesDefaultPrinter ? "\n\nATTENZIONE: stai rimuovendo la stampante predefinita. Windows potrà scegliere un’altra predefinita. L’app non ne imposta una al suo posto." : "") +
        (RemovesUnverifiedQueues ? "\n\nATTENZIONE: la selezione include code la cui appartenenza a ISAFOM non è verificata. Porta, IP, driver in comune o configurazione incompleta aiutano la scelta. Controlla ogni coda prima di confermare." : "") +
        "\n\nPorte dedicate da eliminare: " + string.Join(", ", Ports.Select(p => p.Name).DefaultIfEmpty("nessuna")) +
        "\nDriver inutilizzati da eliminare: " + string.Join(", ", Drivers.Select(d => d.Name).DefaultIfEmpty("nessuno")) +
        "\nPacchetti verificati da eliminare: " + string.Join(", ", Packages.Select(p => p.Name).DefaultIfEmpty("nessuno")) +
        "\nFile dedicati verificati da eliminare: " + (Files??[]).Length +
        (Retained.Length > 0 ? "\n\nRisorse conservate:\n• " + string.Join("\n• ", Retained) : "") +
        ((Unresolved??[]).Length > 0 ? "\n\nResidui da verificare manualmente:\n• " + string.Join("\n• ", Unresolved!) : "") +
        "\n\nLe dipendenze vengono ricontrollate durante la rimozione. Una risorsa può essere conservata se diventa necessaria ad altre code.";
}
public sealed record SelectiveQueueResult(string QueueId, string Name, string Outcome, string[] Residuals, string[] Retained, string Details)
{
    public bool Complete => Outcome == "Rimossa completamente";
    public string Description => $"{Name}: {Outcome}" + (Details.Length > 0 ? "\n" + Details : "") +
        (Residuals.Length > 0 ? "\nResidui:\n• " + string.Join("\n• ", Residuals) : "") +
        (Retained.Length > 0 ? "\nRisorse conservate:\n• " + string.Join("\n• ", Retained) : "");
}

internal static class SelectiveRemovalService
{
    internal static Task<SelectiveQueue[]> InventoryAsync(PrinterSettings settings) =>
        PowerShellBackend.RunAsync<SelectiveQueue[]>(new { Mode = "SelectiveInventory", Settings = settings }, CancellationToken.None);
    internal static Task<SelectiveRemovalPlan> PreviewAsync(PrinterSettings settings, string[] queueIds) =>
        PowerShellBackend.RunAsync<SelectiveRemovalPlan>(new { Mode = "PlanSelectiveRemoval", Settings = settings, QueueIds = queueIds }, CancellationToken.None);
    internal static async Task<SelectiveQueueResult[]> RemoveAsync(PrinterSettings settings, SelectiveRemovalPlan approved)
    {
        var results=await MachineElevation.RemoveSelectedAsync(settings, approved.Fingerprint, approved.Queues.Select(q => q.QueueId).ToArray());
        if(!results.Select(r=>r.QueueId).Order(StringComparer.Ordinal).SequenceEqual(approved.Queues.Select(q=>q.QueueId).Order(StringComparer.Ordinal)))
            throw new InstallerException(ErrorCode.Protocol);
        return results;
    }
}

// Selection and confirmation are separate steps. Refresh and every selection
// change invalidate the reviewed plan. No row is ever selected automatically.
internal sealed class SelectiveRemovalWindow : Window
{
    internal Button RefreshButton { get; } = new() { Content = "Aggiorna elenco", Padding = new(12, 7, 12, 7) };
    internal Button ReviewButton { get; } = new() { Content = "Controlla selezione…", IsEnabled = false, Padding = new(12, 7, 12, 7) };
    internal Button RemoveButton { get; } = new() { Content = "Rimuovi le code confermate", IsEnabled = false, Padding = new(12, 7, 12, 7) };
    internal CheckBox ShowOthers { get; } = new() { Content = "Mostra anche le altre stampanti", Margin = new(0, 10, 0, 10) };
    internal CheckBox Acknowledgement { get; } = new() { Content = new TextBlock {Text="Confermo esplicitamente la rimozione delle sole code elencate nel riepilogo.",TextWrapping=TextWrapping.Wrap}, IsEnabled = false, Margin = new(0, 10, 0, 10) };
    internal CheckBox DefaultAcknowledgement { get; } = new() { Content = new TextBlock { Text = "Confermo anche la rimozione della stampante predefinita indicata nel riepilogo. Windows potrà scegliere un’altra predefinita.", TextWrapping = TextWrapping.Wrap }, IsEnabled = false, Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 10) };
    internal CheckBox UnverifiedQueuesAcknowledgement { get; } = new() { Content = new TextBlock { Text = "Confermo la rimozione delle code non riconosciute come ISAFOM elencate nel riepilogo, dopo averne controllato i motivi.", TextWrapping = TextWrapping.Wrap }, IsEnabled = false, Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 10) };
    internal List<CheckBox> QueueChoices { get; } = [];
    internal SelectiveRemovalPlan? ApprovedPlan { get; private set; }
    private SelectiveQueue[] inventory = [];
    private readonly StackPanel rows = new();
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Func<Task<SelectiveQueue[]>> load;
    private readonly Func<string[], Task<SelectiveRemovalPlan>> preview;
    private readonly Isafom.Collector.WindowPlacement placement;
    private bool busy;

    internal SelectiveRemovalWindow(Func<Task<SelectiveQueue[]>> load, Func<string[], Task<SelectiveRemovalPlan>> preview)
    {
        this.load = load; this.preview = preview;
        Title = "Rimozione forzata selettiva ISAFOM";
        Width = 920; Height = 740; MinWidth = 360; MinHeight = 320;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 251));
        Icon = Application.Current?.MainWindow?.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(18) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = new Button { Content = "Annulla", IsCancel = true, Padding = new(12, 7, 12, 7) };
        foreach (var button in new[] { cancel, RefreshButton, ReviewButton, RemoveButton })
        { button.Margin = new(4); actions.Children.Add(button); }
        dock.Children.Add(actions);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "Seleziona le code da rimuovere", FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Seleziona una o più code. Sono selezionabili le code ISAFOM/Kyocera, quelle con la stessa porta, IP/host o driver e quelle con configurazione incompleta. Ogni riga ne indica i motivi. Le altre restano in grigio. Nessuna selezione iniziale. Le code condivise, remote, di sistema e con identità non verificabile restano protette.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) });
        content.Children.Add(ShowOthers); content.Children.Add(rows);
        content.Children.Add(summary); content.Children.Add(Acknowledgement); content.Children.Add(DefaultAcknowledgement); content.Children.Add(UnverifiedQueuesAcknowledgement);
        dock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        ReviewButton.Click += async (_, _) => await ReviewAsync();
        ShowOthers.Checked += (_, _) => RenderRows(); ShowOthers.Unchecked += (_, _) => RenderRows();
        Acknowledgement.Checked += (_, _) => UpdateActions(); Acknowledgement.Unchecked += (_, _) => UpdateActions();
        DefaultAcknowledgement.Checked += (_, _) => UpdateActions(); DefaultAcknowledgement.Unchecked += (_, _) => UpdateActions();
        UnverifiedQueuesAcknowledgement.Checked += (_, _) => UpdateActions(); UnverifiedQueuesAcknowledgement.Unchecked += (_, _) => UpdateActions();
        RemoveButton.Click += (_, _) => { if (CanConfirmRemoval) DialogResult = true; };
        Loaded += async (_, _) => await RefreshAsync();
        placement = new(this, 920, 740, minimumHeight: 320, minimumWidth: 360); placement.Attach();
    }

    private void InvalidatePlan()
    { ApprovedPlan = null; Acknowledgement.IsChecked = false; DefaultAcknowledgement.IsChecked = false; UnverifiedQueuesAcknowledgement.IsChecked = false; summary.Text = ""; UpdateActions(); }
    private bool CanConfirmRemoval => !busy && ApprovedPlan is not null && Acknowledgement.IsChecked == true &&
        (!ApprovedPlan.RemovesDefaultPrinter || DefaultAcknowledgement.IsChecked == true) &&
        (!ApprovedPlan.RemovesUnverifiedQueues || UnverifiedQueuesAcknowledgement.IsChecked == true);
    private void UpdateActions()
    {
        RefreshButton.IsEnabled = !busy; ShowOthers.IsEnabled = !busy;
        foreach (var choice in QueueChoices) choice.IsEnabled = !busy && ((SelectiveQueue)choice.Tag).CanSelect;
        ReviewButton.IsEnabled = !busy && QueueChoices.Any(c => c.IsChecked == true);
        Acknowledgement.IsEnabled = !busy && ApprovedPlan is not null;
        DefaultAcknowledgement.Visibility = ApprovedPlan?.RemovesDefaultPrinter == true ? Visibility.Visible : Visibility.Collapsed;
        DefaultAcknowledgement.IsEnabled = !busy && ApprovedPlan?.RemovesDefaultPrinter == true;
        UnverifiedQueuesAcknowledgement.Visibility = ApprovedPlan?.RemovesUnverifiedQueues == true ? Visibility.Visible : Visibility.Collapsed;
        UnverifiedQueuesAcknowledgement.IsEnabled = !busy && ApprovedPlan?.RemovesUnverifiedQueues == true;
        RemoveButton.IsEnabled = CanConfirmRemoval;
    }
    internal void SetInventory(SelectiveQueue[] value)
    { inventory = value; RenderRows(); }
    private void RenderRows()
    {
        InvalidatePlan(); rows.Children.Clear(); QueueChoices.Clear();
        foreach (var q in inventory.Where(q => q.Associated || q.CanSelect || ShowOthers.IsChecked == true))
        {
            var label = new TextBlock { Text = $"{q.Name}\nDriver: {q.DriverName}   Porta: {q.PortName}\nIP/host: {(q.Address.Length > 0 ? q.Address : "non disponibile")}   Stato: {q.Status}\n{q.Association}" +
                (q.IsDefault ? "\nPredefinita Windows" : "") +
                (q.SelectionReasons.Length > 0 ? "\nMotivi: " + q.SelectionReasons : "") +
                (q.Protection.Length > 0 ? "\nProtetta: " + q.Protection : "") + "\nID Windows: " + (q.QueueId.Length > 0 ? q.QueueId : "non disponibile"), TextWrapping = TextWrapping.Wrap };
            var choice = new CheckBox { Content = label, Tag = q, IsChecked = false, IsEnabled = q.CanSelect, Margin = new(8) };
            choice.Checked += (_, _) => InvalidatePlan(); choice.Unchecked += (_, _) => InvalidatePlan();
            QueueChoices.Add(choice);
            rows.Children.Add(new Border { Child = choice, Background = q.Associated ? new SolidColorBrush(Color.FromRgb(232, 243, 255)) : Brushes.White, Margin = new(0, 0, 0, 8), CornerRadius = new(5) });
        }
        if (rows.Children.Count == 0) rows.Children.Add(new TextBlock { Text = "Nessuna coda ISAFOM o Kyocera selezionabile rilevata. Puoi visualizzare le altre stampanti o aggiornare l’elenco.", TextWrapping = TextWrapping.Wrap });
        UpdateActions();
    }
    internal async Task RefreshAsync()
    {
        InvalidatePlan(); busy = true; UpdateActions();
        try { SetInventory(await load()); }
        catch (Exception) { SetInventory([]); summary.Text = "Impossibile aggiornare l’inventario. Verifica il servizio di stampa e i permessi, poi riprova."; }
        finally { busy = false; UpdateActions(); }
    }
    internal async Task ReviewAsync()
    {
        string[] ids = QueueChoices.Where(c => c.IsChecked == true).Select(c => ((SelectiveQueue)c.Tag).QueueId).ToArray();
        InvalidatePlan(); if (ids.Length == 0) return;
        busy = true; UpdateActions();
        try { ApprovedPlan = await preview(ids); summary.Text = ApprovedPlan.Description; }
        catch (Exception) { summary.Text = "La selezione non è più valida o non è verificabile. Aggiorna l’elenco e controlla nuovamente le code."; }
        finally { busy = false; UpdateActions(); }
    }
}
