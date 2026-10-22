using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Isafom.Collector;

namespace Isafom.ConfigExtractor;

internal sealed class IdentityVerifierWindow:Window
{
    private readonly TextBox name=new(){FontSize=15,Padding=new Thickness(6),MinHeight=34};
    private readonly PasswordBox account=new(){FontSize=15,Padding=new Thickness(6),MinHeight=34};
    private readonly Button verify=new(){Content="Verifica",Padding=new Thickness(22,8,22,8),IsDefault=true,IsEnabled=false};
    private readonly TextBlock result=new(){TextWrapping=TextWrapping.Wrap,FontSize=14,Margin=new Thickness(0,18,0,0)};
    private bool busy;
    internal IdentityVerifierWindow(QueueChoice choice)
    {
        Title="ISAFOM — Verifica della configurazione Kyocera";
        Width=700;Height=620;MinWidth=480;MinHeight=430;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        NameScope.SetNameScope(this,new NameScope());RegisterName("UserNameInput",name);RegisterName("AccountCodeInput",account);RegisterName("VerifyButton",verify);
        var body=new StackPanel{Margin=new Thickness(24)};
        body.Children.Add(new TextBlock{Text="Verifica delle impostazioni attuali",FontSize=23,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        body.Children.Add(new TextBlock{Text="Inserisci i due dati che vuoi confrontare con la Kyocera già installata. La verifica legge le impostazioni e lascia invariata la configurazione.",Margin=new Thickness(0,10,0,20),TextWrapping=TextWrapping.Wrap,FontSize=14});
        body.Children.Add(Label("Nome utente, come nel driver"));body.Children.Add(name);
        body.Children.Add(Label("Account ID / codice personale"));body.Children.Add(account);
        body.Children.Add(new TextBlock{Text="Il codice conserva gli zeri iniziali. La configurazione comune con la stampa privata è validata per quattro cifre.",Margin=new Thickness(0,6,0,18),TextWrapping=TextWrapping.Wrap,FontSize=12});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(verify);
        var close=new Button{Content="Chiudi",Margin=new Thickness(10,0,0,0),Padding=new Thickness(20,8,20,8)};close.Click+=(_,_)=>Close();buttons.Children.Add(close);
        body.Children.Add(buttons);body.Children.Add(result);
        Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        name.TextChanged+=(_,_)=>UpdateEnabled();account.PasswordChanged+=(_,_)=>UpdateEnabled();
        verify.Click+=async(_,_)=>
        {
            IdentityVerification.Request input;
            try
            {
                string request=JsonSerializer.Serialize(new{UserName=name.Text,AccountId=account.Password});
                input=IdentityVerification.ReadRequest(new StringReader(request));
            }
            catch(ArgumentException){result.Text="Inserisci un nome valido e un Account ID da una a otto cifre ASCII. Il nome deve rientrare nel limite di 128 caratteri previsto per questa verifica.";return;}
            busy=true;UpdateEnabled();result.Text="Lettura delle impostazioni in corso…";
            try
            {
                var report=await Task.Run(()=>IdentityVerification.Check(choice,input));
                if(IsVisible)result.Text=Describe(report);
            }
            catch(Exception){if(IsVisible)result.Text="Non è stato possibile completare la lettura. Nessuna impostazione applicata.";}
            finally{busy=false;if(IsVisible)UpdateEnabled();}
        };
        Closed+=(_,_)=>{name.Clear();account.Clear();};
        Loaded+=(_,_)=>
        {
            var area=SystemParameters.WorkArea;
            MinWidth=Math.Min(MinWidth,area.Width);MinHeight=Math.Min(MinHeight,area.Height);
            MaxWidth=area.Width;MaxHeight=area.Height;Width=Math.Min(Width,area.Width);Height=Math.Min(Height,area.Height);
            name.Focus();
        };
    }
    private void UpdateEnabled()=>verify.IsEnabled=!busy&&!string.IsNullOrWhiteSpace(name.Text)&&account.Password.Length>0;
    private static TextBlock Label(string text)=>new(){Text=text,Margin=new Thickness(0,10,0,5),FontSize=14,FontWeight=FontWeights.SemiBold};
    private static string Match(bool? value)=>value is null?"non verificabile":value.Value?"corrisponde":"non corrisponde";
    internal static string Describe(IdentityVerificationResult report)
    {
        var text=new StringBuilder();
        text.AppendLine(report.NameLiteralObservedInSelectedSnapshots?"Nome: testo rilevato nei dati. L'esatto campo del driver resta da verificare.":"Nome: testo non rilevato nelle aree esaminate; il campo esatto resta non verificabile.");
        foreach(var code in report.Codes)text.AppendLine("Account ID — "+code.Scope+": "+Match(code.AccountMatchesRequested)+".");
        foreach(var mode in report.Modes)text.AppendLine("Modalità accounting letta dal driver — "+mode.Scope+": "+(mode.AccountingMode=="UseSpecificID"?"ID account specifico":mode.AccountingMode)+".");
        var user=report.Codes.FirstOrDefault(c=>c.Scope=="Utente corrente");
        text.AppendLine("Codice privato nel ticket utente: "+Match(user?.PrivateMatchesRequested)+".");
        text.AppendLine("Richiesta password: stato non verificabile con questa interfaccia.");
        if(!report.SharedCodeLengthValidated)text.AppendLine("Per la configurazione comune della stampa privata sono validate soltanto quattro cifre.");
        if(report.Integrity.ConfigurationExcludingChangeIdRestored!=true)text.AppendLine("Lo stato della coda è cambiato durante la lettura o non è completamente leggibile: ripeti la verifica con le finestre del driver chiuse.");
        text.AppendLine();
        text.AppendLine(report.Codes.Any(c=>c.AccountMatchesRequested==false)?"Verifica complessiva: almeno un Account ID non corrisponde. Nome esatto e password privata restano non verificabili automaticamente.":"Verifica complessiva: parziale. Nome esatto e password privata non sono ancora entrambi verificabili automaticamente.");
        text.AppendLine("L'installer automatico resta in sviluppo.");
        return text.ToString();
    }
}
