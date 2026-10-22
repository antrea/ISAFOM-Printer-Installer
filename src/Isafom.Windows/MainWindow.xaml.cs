using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isafom.Core;

namespace Isafom.Windows;

public partial class MainWindow : Window
{
    private readonly SafeEventLog events;
    private readonly InstallationJourney journey = new();
    private readonly Queue<string> liveLines = new(), debugLines = new();
    private bool trackingJourney;
    private readonly DispatcherTimer revealTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool busy;
    internal string SelectedDriverVersion => (DriverChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? DriverProfiles.DefaultVersion;
    private PrinterSettings SelectedSettings() => DriverProfiles.Select(AdminConfiguration.Load(),SelectedDriverVersion);

    private void DriverChoiceChanged(object sender, SelectionChangedEventArgs e) => UpdateDriverChoice();
    private void UpdateDriverChoice()
    {
        if(HeaderSubtitle is null || DebugSummary is null)return; // Initial XAML construction.
        HeaderSubtitle.Text="Kyocera TASKalfa 3554ci KX · "+(SelectedDriverVersion==DriverProfiles.DefaultVersion?"driver 8.5.2405":"driver 8.7.0917");
        previousSnapshot=currentSnapshot=null;personalChecks=null;
        trackingJourney=false;
        foreach(var step in journey.Steps)step.State=JourneyStepState.Pending;
        LogState.Text="PRONTO";
        SetStatus("Pronto per configurare la stampante","Inserisci Nome e Account ID. Driver selezionato: "+(SelectedDriverVersion==DriverProfiles.DefaultVersion?"KX 8.5.2405 (predefinito).":"KX 8.7.0917 (facoltativo)."),"ready");
        DebugSummary.Text="Versione selezionata: "+(SelectedDriverVersion==DriverProfiles.DefaultVersion?"KX 8.5.2405 (predefinito)":"KX 8.7.0917 (facoltativo)")+".\nInserisci Nome e Account ID nella scheda Installazione. Per cambiare una versione già presente, usa prima Rimuovi completamente: le risorse condivise sono protette.";
    }
    private void InstallSelectedDriverClick(object sender,RoutedEventArgs e)
    {
        Pages.SelectedIndex=0;
        InstallClick(sender,e);
    }
    private bool closeWhenIdle;
    private ReadOnlyVerification? previousSnapshot, currentSnapshot;
    private PreferenceChecks? personalChecks;
    private readonly Isafom.Collector.WindowPlacement placement;

    public MainWindow() : this(new SafeEventLog()) { }

    internal MainWindow(SafeEventLog eventLog)
    {
        events = eventLog;
        InitializeComponent();
        UpdateDriverChoice();
        JourneySteps.ItemsSource = journey.Steps;
        placement = new(this, 920, 710, minimumHeight:300, minimumWidth:360, preserveInitialAspect:true);
        placement.Attach();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => ApplyAdaptiveLayout(LayoutRoot.RenderSize, VisualTreeHelper.GetDpi(this).DpiScaleY));
        events.Appended += AppendDebugLine;
        events.Reported += OnStageReported;
        revealTimer.Tick += (_, _) => HideSecret();
        Deactivated += (_, _) => HideSecret();
        Closing += OnClosing;
        Closed += (_, _) => { events.Appended -= AppendDebugLine; events.Reported -= OnStageReported; };
        DataObject.AddPastingHandler(Account, (_, e) =>
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
            var value = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            if (value.Length is < 1 or > 4 || value.Any(c => c is < '0' or > '9')) e.CancelCommand();
        });
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    private void AppendDebugLine(string line) => OnUi(() => AppendBounded(DebugLog, debugLines, line));

    private static void AppendBounded(TextBox box, Queue<string> lines, string line)
    {
        lines.Enqueue(line);
        while (lines.Count > 200) lines.Dequeue();
        box.Text = string.Join(Environment.NewLine, lines);
        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();
    }

    internal void BeginJourney(bool verificationOnly)
    {
        trackingJourney = true;
        journey.Begin(verificationOnly);
        personalChecks = null;
        liveLines.Clear();
        InstallationLog.Clear();
        LogState.Text = "IN CORSO";
        FieldsScroll.ScrollToHome();
        SetStatus(verificationOnly ? "Verifica in corso" : "Installazione in corso",
            InstallationJourney.Message(journey.CurrentStage), "working");
    }

    private void OnStageReported(Stage stage, ErrorCode? error) => OnUi(() =>
    {
        string message = error is null ? InstallationJourney.Message(stage) : Messages.For(error.Value) + " [" + error + "]";
        AppendBounded(InstallationLog, liveLines, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (!trackingJourney) return;
        journey.Observe(stage, error);
        if (error is not null) LogState.Text = "DA CONTROLLARE";
        else if (stage == Stage.Finished) LogState.Text = "COMPLETATO";
        else if (busy) SetStatus("Operazione in corso", message, "working");
    });

    private void SetStatus(string title, string text, string state)
    {
        static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
        bool failure = state == "error", working = state == "working";
        StatusCard.Background = Color(failure ? "#FFF2F0" : working ? "#EDF5FC" : "#EDF8F2");
        StatusCard.BorderBrush = Color(failure ? "#E7BFBA" : working ? "#C4D9EB" : "#BCDCCB");
        StatusIconBackground.Background = Color(failure ? "#B83938" : working ? "#447CA1" : "#3A9666");
        StatusIcon.Text = failure ? "\uE783" : working ? "\uE895" : "\uE73E";
        StatusTitle.Foreground = Color(failure ? "#9A302F" : working ? "#244F70" : "#195745");
        StatusTitle.Text = title;
        StatusText.Text = text;
    }

    private void LayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (UiScale is null) return;
        ApplyAdaptiveLayout(e.NewSize, VisualTreeHelper.GetDpi(this).DpiScaleY);
    }

    internal void ApplyAdaptiveLayout(Size viewport, double dpiScale)
    {
        var sizing = AdaptiveLayout.Fit(viewport, dpiScale);
        UiScale.ScaleX = UiScale.ScaleY = sizing.Scale;
    }

    private void AccountTextInput(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(c => c is < '0' or > '9');
    private void AccountChanged(object sender, RoutedEventArgs e) => HideSecret();
    private static char[] ReadSecret(SecureString password)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var result = new char[password.Length];
            Marshal.Copy(ptr, result, 0, result.Length);
            return result;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
    }
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        HideSecret();
        using var secure = Account.SecurePassword;
        var chars = ReadSecret(secure);
        try { Revealed.Text = new string(chars); }
        finally { Array.Clear(chars); }
        RevealedBorder.Visibility = Visibility.Visible;
        revealTimer.Start();
    }
    private void HideSecret()
    {
        revealTimer.Stop();
        if (Revealed is null) return;
        Revealed.Text = "";
        RevealedBorder.Visibility = Visibility.Collapsed;
    }
    internal void SetBusy(bool value)
    {
        busy = value;
        InstallButton.IsEnabled = VerifyButton.IsEnabled = RevealButton.IsEnabled = UserName.IsEnabled = Account.IsEnabled = !value;
        // Reserve its three pixels while idle, so starting an operation cannot
        // steal space from the input fields.
        Progress.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        CaptureButton.IsEnabled = CompareButton.IsEnabled = ExportButton.IsEnabled = UninstallButton.IsEnabled = ForceUninstallButton.IsEnabled = !value;
        DriverChoice.IsEnabled = InstallSelectedDriverButton.IsEnabled = !value;
        if (!value) trackingJourney = false;
        if (!value && closeWhenIdle) Dispatcher.BeginInvoke(Close);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true; closeWhenIdle = true;
            StatusTitle.Text = "Chiusura richiesta";
            StatusText.Text = "La finestra si chiuderà al termine dell'operazione corrente sulla stampante.";
            return;
        }
        HideSecret();
        Account.Clear();
    }
    private void ShowFailure(Exception error)
    {
        var code = error is InstallerException known ? known.Code :
            error is Win32Exception ? ErrorCode.BackendUnavailable :
            error is System.IO.IOException or UnauthorizedAccessException or System.Printing.PrintSystemException ? ErrorCode.PrinterUnavailable : ErrorCode.Unexpected;
        events.Record(trackingJourney ? journey.CurrentStage : Stage.Verification, code);
        SetStatus("Operazione non completata", Messages.For(code) + "\nCodice: " + code +
            (MachineElevation.LastStep == MachineStep.None ? "" : " · Fase amministrativa: " + MachineElevation.LastStep), "error");
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        HideSecret();
        BeginJourney(verificationOnly:false);
        char[] secret = [];
        try
        {
            UserName.Text = InputRules.Name(UserName.Text);
            using var secure = Account.SecurePassword;
            secret = ReadSecret(secure);
            InputRules.Account(secret);
            SetBusy(true);
            var settings = SelectedSettings();
            var provider = new Kx85Preferences(settings);
            provider.ValidateInputs(UserName.Text, secret);
            var engine = new InstallerEngine(new WindowsMachine(settings), provider, events);
            var result = await engine.InstallAsync(settings, UserName.Text, secret);
            if (!result.ConfigurationVerified) throw new InstallerException(ErrorCode.Verification);
            personalChecks = result.Checks;
            DebugSummary.Text = ChecksText(personalChecks);
            SetStatus(result.Kind == InstallKind.Install ? "Installazione completata" : "Configurazione aggiornata",
                "Nome, Job Accounting, stampa privata e bianco e nero salvati e riletti. Il codice resta nel campo per ripetere la verifica. La prova fisica va effettuata sulla stampante.", "success");
        }
        catch (Exception error) { ShowFailure(error); }
        finally { Array.Clear(secret); SetBusy(false); }
    }

    private async void VerifyClick(object sender, RoutedEventArgs e)
    {
        HideSecret();
        BeginJourney(verificationOnly:true);
        SetBusy(true);
        events.Record(Stage.Verification);
        try
        {
            var settings = SelectedSettings();
            var summary = await ReadOnlyVerification.RunAsync(settings);
            currentSnapshot = summary;
            char[] secret = [];
            try
            {
                using var secure = Account.SecurePassword; secret = ReadSecret(secure);
                var provider = new Kx85Preferences(settings);
                provider.ValidateInputs(InputRules.Name(UserName.Text), secret);
                personalChecks = await provider.VerifyForCurrentUserAsync(settings.PrinterName, UserName.Text.Trim(), secret, CancellationToken.None);
            }
            finally { Array.Clear(secret); }
            events.Record(personalChecks.All ? Stage.Finished : Stage.Verification, personalChecks.All ? null : ErrorCode.Verification);
            SetStatus(personalChecks.All ? "Configurazione locale verificata" : "Configurazione incompleta",
                personalChecks.All ? "Tutti i controlli locali coincidono con i dati inseriti. Trovi il dettaglio nella scheda Debug e verifica. La prova fisica resta da effettuare."
                    : "Alcuni controlli non sono conformi. Consulta il dettaglio nella scheda Debug e verifica.", personalChecks.All ? "success" : "error");
            DebugSummary.Text = summary.ToItalian() + "\n\n" + ChecksText(personalChecks);
        }
        catch (Exception error) { ShowFailure(error); }
        finally { SetBusy(false); }
    }

    private static string ChecksText(PreferenceChecks? checks)
    {
        if (checks is null) return "Dati personali non confrontati: inserisci Nome e Account ID nella scheda Installazione, poi premi Verifica installazione.";
        static string Result(bool value) => value ? "OK" : "NON CONFORME";
        return $"Nome: {Result(checks.UserNameMatches)}\nJob Accounting: {Result(checks.JobAccountingEnabled)}\nAccount ID: {Result(checks.AccountMatches)}\nID nelle Preferenze: {Result(checks.JobAccountingInPreferences)}\nStampa privata: {Result(checks.PrivatePrint)}\nRichiesta password disattivata: {Result(checks.PasswordPromptDisabled)}\nCodice privato: {Result(checks.PrivatePasswordMatches)}\nBianco e nero: {Result(checks.Monochrome)}\nRilettura dopo riapertura: {Result(checks.PersistedAfterReopen)}";
    }

    private async void UninstallClick(object sender, RoutedEventArgs e)
    {
        HideSecret();SetBusy(true);trackingJourney=false;
        foreach(var step in journey.Steps) step.State=JourneyStepState.NotRun;
        liveLines.Clear();InstallationLog.Clear();LogState.Text="ANTEPRIMA";
        events.Record(Stage.RemovalPlanning);
        try
        {
            var settings=SelectedSettings();
            bool startForced=ReferenceEquals(sender,ForceUninstallButton);
            var normalPlan=await RemovalService.PreviewAsync(settings);
            var forcedPlan=startForced||normalPlan.RequiresConfirmation||normalPlan.BlockedCode==ErrorCode.RemovalShared
                ?await RemovalService.PreviewAsync(settings,forceRemoval:true):null;
            var plan=startForced&&forcedPlan is not null?forcedPlan:normalPlan;
            DebugSummary.Text=plan.Description;
            if(closeWhenIdle)return;
            if(!plan.HasWork&&plan.BlockedCode is null)
            {
                events.Record(Stage.RemovalFinished);LogState.Text="GIÀ ASSENTE";
                SetStatus("Nulla da rimuovere","Driver, code, porte e impostazioni ISAFOM risultano già assenti.","success");return;
            }
            if(plan.RequiresConfirmation||(normalPlan.BlockedCode==ErrorCode.RemovalShared&&forcedPlan?.RequiresConfirmation==true))
            {
                var dialog=new RemovalWindow(normalPlan,forcedPlan,startForced) {Owner=this};
                if(dialog.ShowDialog()!=true)
                {
                    events.Record(Stage.RemovalCancelled);LogState.Text="ANNULLATO";
                    SetStatus("Disinstallazione annullata","Nessuna rimozione eseguita.","success");return;
                }
                plan=dialog.SelectedPlan;
            }
            if(!plan.CanProceed)throw new InstallerException(plan.BlockedCode??ErrorCode.RemovalBlocked);
            events.Record(Stage.Removing);LogState.Text="IN CORSO";
            SetStatus("Disinstallazione in corso","Autorizza l'eventuale richiesta UAC. Verranno rimossi solo gli elementi confermati.","working");
            var result=await RemovalService.RemoveAsync(settings,plan,allowSpoolerRestart:true);
            personalChecks=null;previousSnapshot=currentSnapshot;currentSnapshot=null;
            if(result.QueueAbsent){Account.Clear();UserName.Clear();}
            static string Outcome(bool done)=>done?"RIMOSSO / GIÀ ASSENTE":"ANCORA PRESENTE / DA CONTROLLARE";
            string summary="Coda e impostazioni globali: "+Outcome(result.QueueAbsent)+"\nPreferenze del tuo profilo: "+Outcome(result.UserPreferencesCleared)+
                "\nDriver registrati: "+Outcome(result.DriverRemoved)+"\nPacchetti Driver Store: "+Outcome(result.PackageRemoved)+
                "\nTutte le porte coinvolte: "+Outcome(result.PortRemoved)+"\nCache e configurazione del tool: "+Outcome(result.StorageRemoved)+
                (result.DriverFilesRemoved?"\nCopie locali dei file driver: eliminate.":"\nCopie locali dei file driver ancora da verificare: "+result.DriverFilesRemaining)+
                (result.SpoolerRestarted?"\nServizio di stampa riavviato con tutte le code vuote.":"")+
                (result.IsolatedHostsClosed>0?"\nComponenti isolati Kyocera chiusi: "+result.IsolatedHostsClosed+".":"")+
                (result.RestartRequired?"\nWindows richiede un riavvio: il ripristino non è ancora completo.":"");
            DebugSummary.Text=summary;events.Record(Stage.RemovalFinished);
            bool partial=!result.Complete;
            LogState.Text=partial?"DA CONTROLLARE":"COMPLETATO";
            SetStatus(partial?"Ripristino non ancora completo":"Ripristino completo verificato",summary,partial?"error":"success");
        }
        catch(Exception error){ShowFailure(error);LogState.Text="DA CONTROLLARE";}
        finally{SetBusy(false);}
    }

    private async void CaptureClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            previousSnapshot = currentSnapshot;
            personalChecks = null;
            currentSnapshot = await ReadOnlyVerification.RunAsync(SelectedSettings());
            DebugSummary.Text = currentSnapshot.ToItalian() + "\n\n" + ChecksText(personalChecks);
        }
        catch(Exception error) { ShowFailure(error); }
        finally { SetBusy(false); }
    }
    private void CompareClick(object sender, RoutedEventArgs e)
    {
        DebugSummary.Text = previousSnapshot is null || currentSnapshot is null ? "Servono due acquisizioni nella stessa sessione."
            : previousSnapshot == currentSnapshot ? "Nessuna differenza nei controlli pubblici tra le due acquisizioni. I codici non sono inclusi nel confronto."
            : "Sono cambiate le verifiche pubbliche:\n\nPRIMA\n" + previousSnapshot.ToItalian() + "\n\nDOPO\n" + currentSnapshot.ToItalian();
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Report JSON|*.json", FileName = "Verifica_ISAFOM.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(new {
                Version = "0.2.7-rc1", Previous = previousSnapshot, Current = currentSnapshot, PersonalChecks = personalChecks,
                PhysicalPrintVerified = false, RebootVerified = false, PersonalValuesExported = false
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch(Exception error) { ShowFailure(error); }
    }
}
