# Compilazione su Windows

## Requisiti

- Windows x64, PowerShell **7**, SDK **.NET 10.0.402** o una patch ammessa da `global.json`.
- Gli archivi originali Kyocera esatti descritti in [driver/README.md](driver/README.md).
- Per la prima compilazione, accesso a NuGet oppure cache già popolata dei pacchetti runtime .NET/Windows Desktop x64.

Non è richiesto Python per compilare l'installer. La compilazione non installa driver, non modifica stampanti e non invia stampe. I test ordinari possono leggere l'inventario locale; i test di rimozione usano inventari simulati.

## Procedura completa

Eseguire dalla radice del repository in PowerShell 7:

```powershell
.\build\Prepare-Kx87.ps1 -Version 8.5.2405 -OfficialArchive 'C:\Driver\Kx85_UPD_8.5.2405_en_RC2_WHQL.zip'
.\build\Prepare-Kx87.ps1 -Version 8.7.0917 -OfficialArchive 'C:\Driver\Kx87_UPD_8.7.0917_en_RC3_signed.zip'
.\build\Test-DriverPayloads.ps1
.\build\Build-Installer.ps1 -RestoreOnline
```

`-RestoreOnline` permette il ripristino dei pacchetti da NuGet; non scarica l'SDK né i driver. Con cache pronta, omettere l'opzione per usare solo i pacchetti locali. Se `dotnet.exe` non è nel PATH, passare `-Dotnet 'C:\Program Files\dotnet\dotnet.exe'`; `-PackageCache` permette una cache alternativa.

La build produce **`dist/installer-0.2.7-rc1/Installa_Stampante_ISAFOM.exe`** e `dist/SHA256SUMS.txt`. I file intermedi rimangono nella cartella `.lavoro-installer023` del repository, nome storico dello script. Non vengono scaricati ambienti di analisi o copie di SDK. `-SkipTests` è disponibile per sviluppo, ma non costituisce una validazione.

Il programma è pubblicato autonomo, x64, in singolo file, con entrambi i payload. Un nuovo hash non coincide necessariamente con quello della build locale di riferimento: toolchain, percorsi e contenuto influiscono sul risultato. Non attribuire le prove native precedenti a un nuovo eseguibile senza ripeterle.

## Verifica dell'EXE appena costruito

```powershell
.\tests\Test-PublishedApp.ps1
Get-FileHash .\dist\installer-0.2.7-rc1\Installa_Stampante_ISAFOM.exe -Algorithm SHA256
```

Il test esegue rendering, convalida configurazione e anteprime reali di sola lettura. Richiede i normali servizi di stampa Windows disponibili; non manda stampe. Restano separati i [collaudi sul campo](VALIDATION.md).

## Compilare raccoglitore, estrattore e prototipo

```powershell
.\build\Build-Tools.ps1 -Tool Collector -RestoreOnline
.\build\Build-Tools.ps1 -Tool Extractor -RestoreOnline
.\build\Build-Tools.ps1 -Tool Readback -RestoreOnline
.\build\Build-Tools.ps1 -Tool Sentinel -RestoreOnline
```

Collector produce il raccoglitore autonomo in `dist/collector-0.3.0`. Extractor e Readback producono versioni dipendenti dal runtime .NET 10 Windows Desktop nelle directory `dist/config-extractor-0.1.2` e `dist/kx-reversed-readback-0.1.3`, compatibili con i wrapper storici. Sentinel produce il prototipo dipendente dal runtime in `dist/kx-sentinelle-0.1.6`: la sua esecuzione può modificare code di laboratorio. Compilare non significa avviare questi tool.

Le versioni dei tool ausiliari sono storiche e indipendenti da quella dell'installer. Leggere [DIAGNOSTICA.md](docs/DIAGNOSTICA.md) prima dell'uso.

## Aggiornamento driver e manutenzione

`DriverProfiles.cs`, hash dei payload, cataloghi, formati privati e manifest dei file devono restare coerenti. Una nuova versione non si abilita sostituendo soltanto lo ZIP. Preparare un profilo dedicato e ripetere scrittura, rilettura, privacy e rimozione. `build/Prepare-SpoolFileManifest.ps1` rigenera l'elenco hash dei file noti dagli archivi locali; rivedere ogni differenza.

Le icone sono riproducibili con `build/Generate-AppIcon.ps1`. L'override amministrativo è documentato nella [guida amministratore](docs/GUIDA_AMMINISTRATORE.md).

## Pubblicazione

Non caricare l'EXE attuale o gli ZIP dei driver su GitHub: incorporano software proprietario con restrizioni sulla pubblicazione. `build/Package-PublicSources.ps1` crea un archivio **dei soli file tracciati da Git** e lo verifica contro i binari vietati. Eseguire dopo il commit. Per un pacchetto interno, conservare licenze e avvisi e verificare le condizioni di distribuzione prima di condividerlo.
