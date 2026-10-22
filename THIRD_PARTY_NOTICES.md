# Componenti di terze parti e distribuzione

## Kyocera

Kyocera TASKalfa e KX sono nomi del rispettivo titolare. Il progetto non modifica i file del driver e non è una distribuzione ufficiale Kyocera.

Gli archivi 8.5.2405 RC2 WHQL e 8.7.0917 RC3 signed usati localmente comprendono `Setup/EULA/License_EN.rtf` e `License_IT.rtf`. L'accordo inglese, alla clausola d), esclude il caricamento del software in posizioni elettronicamente accessibili al pubblico. Per questa pubblicazione sono esclusi archivi driver, cataloghi, librerie, estratti di codice/disassemblati e l'installer autonomo che incorpora i driver. Le licenze originali si consultano negli archivi ottenuti dal fornitore; non sono sostituite da questo documento.

Il repository contiene codice originale di integrazione e ricerca, nomi tecnici, offset di interoperabilità, hash e manifest. Le restrizioni sui componenti proprietari non vengono trasformate in una licenza open source. Chi costruisce o distribuisce il programma deve verificare i termini applicabili al proprio utilizzo.

## Microsoft .NET

La pubblicazione autonoma locale incorpora il runtime .NET. Gli avvisi provenienti dalla distribuzione usata per il progetto sono conservati in [docs/licenses/DOTNET-LICENSE.txt](docs/licenses/DOTNET-LICENSE.txt) e [docs/licenses/DOTNET-ThirdPartyNotices.txt](docs/licenses/DOTNET-ThirdPartyNotices.txt). I runtime e l'SDK non sono copiati nel repository.

## Strumenti di ricerca opzionali

Gli script di analisi possono utilizzare pefile, Capstone, Unicorn e REA. Nessun relativo ambiente, cache, JDK, Ghidra o libreria installata è incluso nel repository. REA è referenziato dal [repository dell'autore](https://github.com/morluto/rea); per installazione e licenza consultare i rispettivi progetti. Non sono requisiti per usare l'installer compilato.

## Icone e schermate

Le icone del programma sono generate da [build/Generate-AppIcon.ps1](build/Generate-AppIcon.ps1). La schermata pubblicata proviene dal rendering del programma con campi vuoti. I nomi degli enti e i marchi identificano la destinazione del tool e non attribuiscono approvazioni da parte dei rispettivi titolari.
