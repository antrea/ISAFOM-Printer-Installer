using System.ComponentModel;
using Isafom.Core;

namespace Isafom.Windows;

public enum JourneyStepState { Pending, Active, Completed, Failed, NotRun }

public sealed class JourneyStep(int number, string label) : INotifyPropertyChanged
{
    private JourneyStepState state;
    public int Number { get; } = number;
    public string Label { get; } = label;
    public bool First => Number == 1;
    public bool Last => Number == 5;
    public JourneyStepState State
    {
        get => state;
        internal set { state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    }
    public string Symbol => State switch { JourneyStepState.Completed => "✓", JourneyStepState.Failed => "!", JourneyStepState.NotRun => "–", _ => Number.ToString() };
    public string Description => State switch
    {
        JourneyStepState.Active => "In corso", JourneyStepState.Completed => "Completato",
        JourneyStepState.Failed => "Da controllare", JourneyStepState.NotRun => "Non eseguito", _ => "In attesa"
    };
    public string AccessibleDescription => Label + ": " + Description;
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Progress is driven by engine events. Driver/port/queue are intentionally grouped:
// the elevated machine operation does not report intermediate successful steps.
internal sealed class InstallationJourney
{
    public JourneyStep[] Steps { get; } = [new(1,"Controlli"),new(2,"Connessione"),new(3,"Driver e coda"),new(4,"Preferenze"),new(5,"Verifica finale")];
    public Stage CurrentStage { get; private set; } = Stage.Inspecting;
    private bool verificationOnly;

    public void Begin(bool verifyOnly)
    {
        verificationOnly = verifyOnly;
        CurrentStage = verifyOnly ? Stage.Verification : Stage.Inspecting;
        foreach (var step in Steps) step.State = verifyOnly ? JourneyStepState.NotRun : JourneyStepState.Pending;
        Steps[Index(CurrentStage)].State = JourneyStepState.Active;
    }

    public void Observe(Stage stage, ErrorCode? error)
    {
        CurrentStage = stage;
        int index = Index(stage);
        if (error is not null) { Steps[index].State = JourneyStepState.Failed; return; }
        if (stage == Stage.Finished)
        {
            foreach (var step in Steps.Where(s => !verificationOnly || s.Last)) step.State = JourneyStepState.Completed;
            return;
        }
        if (!verificationOnly)
            for (int i = 0; i < index; i++) Steps[i].State = JourneyStepState.Completed;
        Steps[index].State = JourneyStepState.Active;
    }

    private static int Index(Stage stage) => stage switch
    {
        Stage.Inspecting => 0, Stage.Network => 1, Stage.Driver or Stage.Queue => 2,
        Stage.Preferences => 3, _ => 4
    };

    public static string Message(Stage stage) => stage switch
    {
        Stage.Inspecting => "Controllo di Windows e della configurazione esistente…",
        Stage.Network => "Verifica della connessione alla stampante…",
        Stage.Driver => "Preparazione del driver, della porta e della coda di stampa…",
        Stage.Queue => "Configurazione della coda di stampa…",
        Stage.Preferences => "Salvataggio di Nome, Job Accounting, stampa privata e bianco e nero…",
        Stage.Verification => "Rilettura e confronto delle impostazioni salvate…",
        Stage.Finished => "Tutti i controlli locali sono stati superati.",
        Stage.RemovalPlanning => "Controllo degli elementi ISAFOM rimovibili: nessuna modifica in questa fase…",
        Stage.Removing => "Rimozione degli elementi confermati e verifica delle dipendenze del driver…",
        Stage.RemovalFinished => "Procedura di rimozione terminata. Consulta il riepilogo nella scheda Debug.",
        Stage.RemovalCancelled => "Disinstallazione annullata: nessuna rimozione eseguita.",
        _ => "Operazione in corso…"
    };
}
