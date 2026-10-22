using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Isafom.Collector;

internal sealed record StorageRow(string Coda, string Fonte, string Campo, string Nome, string Tipo, string Byte, string Esito);

public partial class CollectorWindow : Window
{
    private readonly CollectorSession session = new();
    private readonly CancellationTokenSource cancellation = new();
    private bool closed;
    private bool busy;
    private bool inventoryLoaded;
    public CollectorWindow()
    {
        InitializeComponent();
        new WindowPlacement(this).Attach();
        Loaded += async (_, _) => { if (!inventoryLoaded && !busy) await RefreshQueues(); };
        session.Progress += entry => Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            LogText.AppendText(entry + Environment.NewLine); LogText.ScrollToEnd();
            Status.Text = entry;
        });
        Closed += (_, _) => { closed = true; cancellation.Cancel(); session.Dispose(); cancellation.Dispose(); };
    }
    private void ViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Wrapping instructions/buttons need extra vertical room on narrow
        // displays. Keep the whole grid in the outer scrollable extent.
        LayoutGrid.MinHeight = e.NewSize.Width < 760 ? 860 : 620;
    }
    private void SetBusy(bool value)
    {
        busy = value;
        CaptureButton.IsEnabled = !value && session.SelectedQueue is not null;
        QueuePicker.IsEnabled = RefreshQueuesButton.IsEnabled = !value;
        BaselineButton.IsEnabled = ExportButton.IsEnabled = !value && session.HasCapture;
        ProgressBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshQueuesClick(object sender, RoutedEventArgs e) => await RefreshQueues();
    private async Task RefreshQueues()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await session.RefreshQueuesAsync(cancellation.Token);
            if (closed) return;
            inventoryLoaded = true;
            var selected = session.SelectedQueue;
            QueuePicker.ItemsSource = session.AvailableQueues;
            QueuePicker.SelectedItem = selected;
            Status.Text = session.DiscoveryAvailable
                ? $"Rilevate {session.AvailableQueues.Length} stampanti. Scegli quella funzionante dall'elenco."
                : "Elenco non leggibile da entrambe le fonti Windows. Nessuna assenza della stampante dedotta.";
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = "Ricerca interrotta o tempo massimo superato."; }
        catch { if (!closed) Status.Text = "Elenco non disponibile. Premi Aggiorna elenco nella normale sessione dell'utente che stampa."; }
        finally
        {
            if (!closed)
            {
                SetBusy(false);
                if (session.SelectedQueue is null)
                {
                    QueueDetails.Text = "Scegli la stampante funzionante dall'elenco. Nomi e driver non saranno copiati nel rapporto.";
                    if (!session.HasCapture) { StorageGrid.ItemsSource = ComparisonGrid.ItemsSource = null; }
                }
            }
        }
    }
    private void QueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (busy) return;
        session.SelectQueue(QueuePicker.SelectedItem as QueueChoice);
        CaptureButton.Content = "Raccogli impostazioni";
        StorageGrid.ItemsSource = ComparisonGrid.ItemsSource = null;
        var selected = session.SelectedQueue;
        QueueDetails.Text = selected is null ? "Scegli la coda usata per stampare: l'elenco non è limitato al nome del driver previsto."
            : "Driver: " + selected.DriverName + " · Porta: " + selected.PortName;
        Summary.Text = "La prossima raccolta riguarderà esclusivamente la stampante scelta. Il nome reale della coda e il nome del driver non standard sono mostrati solo localmente. Job Accounting e stampa privata saranno letti dove possibile; i dati proprietari non decodificabili resteranno segnalati come tali.";
        Status.Text = selected is null ? "Seleziona una stampante." : "Stampante selezionata. Premi Raccogli impostazioni.";
        SetBusy(false);
    }
    private async void CaptureClick(object sender, RoutedEventArgs e)
    {
        if (busy || session.SelectedQueue is null) return;
        SetBusy(true);
        try
        {
            await session.CollectAsync(cancellation.Token);
            if (closed) return;
            RefreshResults();
            CaptureButton.Content = "Rileggi e confronta";
            Status.Text = "Acquisita la coda scelta e raccolte le relative fonti. Esporta lo ZIP per l'analisi; le letture non disponibili sono indicate nel rapporto.";
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = "Lettura annullata o tempo massimo superato."; }
        catch { if (!closed) Status.Text = "Coda scelta non acquisita. Nessun rapporto vuoto dichiarato completo: aggiorna l'elenco, seleziona nuovamente la stampante e riprova."; }
        finally
        {
            if (!closed)
            {
                SetBusy(false);
            }
        }
    }
    private void RefreshResults()
    {
        var bundle = session.Bundle();
        Summary.Text = ReportWriter.ToMarkdown(bundle);
        var rows = new List<StorageRow>();
        foreach (var queue in bundle.Current.Evidence)
        {
            foreach (var area in queue.Registry)
            {
                rows.Add(new(queue.Alias, area.Area, "Area", area.Location, "", "", area.State));
                foreach (var v in area.Values)
                    rows.Add(new(queue.Alias, area.Area, v.Slot, v.Name, v.Kind, v.Bytes.ToString(), v.SafeValue ?? v.Meaning));
            }
            foreach (var d in queue.DevModes)
                rows.Add(new(queue.Alias, d.Scope, "DEVMODE", "Parte privata: solo dimensioni", "Binary", d.PrivateBytes?.ToString() ?? "?", d.State));
            foreach (var p in queue.PrintSchema)
                rows.Add(new(queue.Alias, p.Scope, "PrintSchema", p.Feature, "XML filtrato", "", p.Option is null ? p.State : p.Option + " — " + p.State));
        }
        StorageGrid.ItemsSource = rows;
        ComparisonGrid.ItemsSource = bundle.Differences.Select(d => new { Coda = d.Queue, Fonte = d.Source, Campo = d.Field, Esito = d.Result }).ToArray();
    }
    private void BaselineClick(object sender, RoutedEventArgs e)
    {
        session.ResetBaseline(); RefreshResults(); Status.Text = "Riferimento aggiornato. La prossima lettura sarà confrontata con questa.";
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salva il pacchetto diagnostico condivisibile", Filter = "Archivio diagnostico ZIP (*.zip)|*.zip",
            FileName = "ISAFOM_Raccolta_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip", OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName)) { Status.Text = "Scegli un nome nuovo: i pacchetti esistenti non vengono sovrascritti."; return; }
        try { ReportWriter.Export(dialog.FileName, session.Bundle()); Status.Text = "ZIP salvato: " + dialog.FileName; }
        catch { Status.Text = "Esportazione non completata. Scegli una cartella scrivibile e un nome nuovo."; }
    }
}
