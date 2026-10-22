using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

// The study observes manual changes only. No setter, registry write, printer
// dialog automation, print command or automatic restore is implemented.
public sealed class SentinelWindow : Window
{
    private readonly string workRoot;
    private readonly QueueChoice queue;
    private readonly ComboBox field=new(){MinHeight=34,Margin=new(0,8,0,8)};
    private readonly TextBox token=new(){IsReadOnly=true,FontFamily=new("Consolas"),FontSize=20,MinHeight=38,Margin=new(0,8,0,8)};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)};
    private readonly TextBox result=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=160,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button baselineButton=new(){Content="1. Leggi prima"};
    private readonly Button changedButton=new(){Content="2. Confronta dopo la modifica",IsEnabled=false};
    private readonly Button restoredButton=new(){Content="3. Verifica il ripristino",IsEnabled=false};
    private SentinelSnapshot? baseline;
    private SentinelSnapshot? changed;
    private bool busy;
    private bool restorationVerified;
    public SentinelWindow(string workRoot,QueueChoice queue)
    {
        this.workRoot=workRoot;this.queue=queue;
        Title="ISAFOM — Studio con sentinella (solo osservazione)";Width=880;Height=740;MinWidth=680;MinHeight=520;
        var panel=new StackPanel{Margin=new(24)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text="Una modifica alla volta, poi ripristino",FontSize=24,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="Il tool legge registro e DEVMODE della sola coda ISAFOM. Le eventuali modifiche si fanno manualmente nella GUI Kyocera, soltanto dopo autorizzazione alla prova. Prima di iniziare devi conoscere il valore originale da reinserire. Nessuna stampa durante lo studio.",TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,8)});
        field.ItemsSource=new[]{"Nome utente","Account ID — Amministratore","Account ID — Preferenze Lavoro","Password privata"};field.SelectedIndex=0;panel.Children.Add(field);
        panel.Children.Add(new TextBlock{Text="Sentinella temporanea: visibile soltanto qui, esclusa dal rapporto e dai log.",TextWrapping=TextWrapping.Wrap});panel.Children.Add(token);
        field.SelectionChanged+=(_,_)=>Generate();Generate();
        var buttons=new WrapPanel();panel.Children.Add(buttons);
        foreach(var button in new[]{baselineButton,changedButton,restoredButton}){button.Margin=new(0,6,8,6);button.Padding=new(12,8,12,8);buttons.Children.Add(button);}
        panel.Children.Add(status);panel.Children.Add(result);
        status.Text="Nessuna lettura di confronto iniziata. Leggi prima; poi modifica SOLO il campo scelto, salva nella GUI e torna qui. Non modificare altri parametri.";
        baselineButton.Click+=async(_,_)=>await Baseline();changedButton.Click+=async(_,_)=>await Changed();restoredButton.Click+=async(_,_)=>await Restored();
        Closing+=(_,e)=>
        {
            if(busy){e.Cancel=true;return;}
            if(baseline is not null&&!restorationVerified&&MessageBox.Show(this,"Il ripristino non risulta verificato. Prima di chiudere ripristina il valore originale nella GUI. Vuoi comunque chiudere lo studio?","Ripristino da verificare",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)e.Cancel=true;
        };
        Closed+=(_,_)=>{baseline?.Dispose();changed?.Dispose();token.Clear();};
    }
    private void Generate()=>token.Text=field.SelectedIndex==0?"ISAFOM_TEST_"+Guid.NewGuid().ToString("N")[..8].ToUpperInvariant():"0"+RandomNumberGenerator.GetInt32(1000).ToString("D3",System.Globalization.CultureInfo.InvariantCulture);
    private void Busy(bool value)
    {
        busy=value;field.IsEnabled=!value&&baseline is null;baselineButton.IsEnabled=!value&&baseline is null;
        changedButton.IsEnabled=!value&&baseline is not null&&changed is null;
        restoredButton.IsEnabled=!value&&changed is not null&&!restorationVerified;
    }
    private async Task Baseline()
    {
        Busy(true);
        try
        {
            var snapshot=await Task.Run(()=>SentinelSnapshot.Capture(queue.Name,queue.DriverName));
            if(!snapshot.Complete){snapshot.Dispose();status.Text="Copertura iniziale parziale. NON modificare il campo: la prova non parte perché non potrebbe certificare il ripristino delle aree previste.";return;}
            if(snapshot.Contains(token.Text)){snapshot.Dispose();Generate();status.Text="Pattern già presente nei dati leggibili: sentinella rigenerata. Ripeti la lettura iniziale.";return;}
            baseline=snapshot;
            status.Text="Prima lettura in memoria. Ora, se hai autorizzato la prova, imposta la sentinella SOLO nel campo selezionato e salva nella GUI Kyocera. Tieni pronto il valore originale per il ripristino. Poi premi 2.";
            result.Text="Copertura iniziale: "+(snapshot.Complete?"letture mirate completate":"parziale: qualche area non leggibile o limite raggiunto")+". Nessun valore originale viene salvato su disco.";
        }
        catch{status.Text="Prima lettura non riuscita. Nessun dettaglio personale registrato.";}
        finally{Busy(false);}
    }
    private async Task Changed()
    {
        Busy(true);
        try
        {
            changed=await Task.Run(()=>SentinelSnapshot.Capture(queue.Name,queue.DriverName));
            var differences=baseline!.Compare(changed,token.Text);
            result.Text=JsonSerializer.Serialize(differences,new JsonSerializerOptions{WriteIndented=true});
            status.Text=$"Rilevate {differences.Length} differenze. Ripristina ORA il valore originale nello stesso campo, salva nella GUI e premi 3. Nessuna posizione binaria è usata per scrivere impostazioni.";
        }
        catch{status.Text="Confronto non riuscito. Ripristina il valore originale prima di chiudere.";}
        finally{Busy(false);}
    }
    private async Task Restored()
    {
        Busy(true);
        try
        {
            using var restored=await Task.Run(()=>SentinelSnapshot.Capture(queue.Name,queue.DriverName));
            var assessment=baseline!.AssessRestored(restored);
            bool? restoredExactly=assessment.AllReadableBytesRestored;
            var report=new SentinelReport(1,(string)field.SelectedItem,DateTime.UtcNow,baseline.Complete,changed!.Complete,restored.Complete,restoredExactly,
                baseline.Compare(changed,token.Text),false,"Un cambiamento o un pattern trovato è un candidato di memorizzazione, non un contratto Kyocera di scrittura. Solo il ChangeID DWORD della radice della coda viene separato dal confronto di configurazione. Nessuna credenziale, impronta, percorso personale, blob o offset è esportato.",assessment);
            string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
            byte[] bytes=Encoding.UTF8.GetBytes(json);
            long existing=new DirectoryInfo(workRoot).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length);
            if(existing+bytes.Length+65536>500L*1024*1024)throw new IOException("WorkspaceBudgetExceeded");
            string output=Path.Combine(workRoot,"raccolta-locale","Studio_Sentinella_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".json");
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(bytes);
            restorationVerified=assessment.ConfigurationExcludingChangeIdRestored==true;result.Text=json;
            status.Text=restorationVerified?"Configurazione leggibile ripristinata. ChangeID di Windows riportato separatamente, se aggiornato. Rapporto filtrato salvato. Nessun metodo di scrittura di produzione certificato.":"Ripristino non verificato: dati di configurazione ancora diversi o copertura parziale. Rapporto filtrato salvato; controlla il campo originale nella GUI e ripeti 3.";
        }
        catch{status.Text="Verifica o salvataggio non completati. Il ripristino resta da verificare. Nessun errore locale grezzo registrato.";}
        finally{Busy(false);}
    }
}
