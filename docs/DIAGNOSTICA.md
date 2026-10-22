# Diagnostica, raccolta e confronto

Sono inclusi tre livelli di strumenti. I sorgenti degli strumenti esplorativi sono conservati per rendere comprensibile il percorso tecnico; il loro stato non sostituisce quello dell'installer corrente.

## Script di sola lettura

`diagnostics/Diagnostica_Kyocera_ISAFOM.ps1` raccoglie dati tecnici filtrati: Windows, coda, driver, porta, impostazioni standard e limiti di verificabilità. Non installa, non stampa, non modifica il registro e non riavvia lo Spooler.

Consultare i parametri prima di eseguire:

```powershell
Get-Help .\diagnostics\Diagnostica_Kyocera_ISAFOM.ps1 -Full
Get-Help .\diagnostics\Confronta_Diagnostica_ISAFOM.ps1 -Full
```

Esempio di raccolta e confronto, usando cartelle nuove:

```powershell
.\diagnostics\Diagnostica_Kyocera_ISAFOM.ps1 -OutputDirectory .\raccolta-locale\riferimento
.\diagnostics\Diagnostica_Kyocera_ISAFOM.ps1 -OutputDirectory .\raccolta-locale\candidato
.\diagnostics\Confronta_Diagnostica_ISAFOM.ps1 -Reference .\raccolta-locale\riferimento\Report.json -Candidate .\raccolta-locale\candidato\Report.json -OutputDirectory .\raccolta-locale\confronto
```

Il secondo script confronta i report prodotti dal primo. Un dato assente o non verificabile non viene interpretato come disabilitato. L'uguaglianza dei report non dimostra l'uguaglianza di campi proprietari che il raccoglitore non può leggere.

## Raccoglitore con GUI ed estrattore

- `src/Isafom.Collector`: raccoglitore con selezione della coda, report e confronto in memoria.
- `src/Isafom.ConfigExtractor`: estrattore/verificatore di impostazioni standard e campi KX. Contiene anche strumenti storici di osservazione con sentinelle.
- `src/Isafom.KxSentinel`: prototipo di scrittura sperimentale su coda di prova. **Non è diagnostica di sola lettura.** Non avviarlo su una stampante operativa senza aver esaminato i comandi e il piano di prova.

Compilare con [build/Build-Tools.ps1](../build/Build-Tools.ps1). I wrapper in `tools/` usano cartelle di output di versioni storiche; la guida di build indica i nomi da usare. Gli script `Prepare-LocalKxLab.ps1` e `Use-GuideDriverInLocalLab.ps1` modificano l'ambiente di prova e non devono essere eseguiti per una semplice raccolta.

## Condivisione dei risultati

I report del progetto escludono i codici personali dalle esportazioni previste, ma possono comunque contenere metadati della coda o della macchina. Esaminarli prima di condividerli; le raccolte reali del progetto non sono pubblicate nel repository. Non allegare il codice, il nome dell'utilizzatore, XML grezzi o esportazioni complete del registro.

Usare una cartella locale dedicata. `raccolta-locale`, `reference-reports`, log, archivi e risultati di build sono esclusi da Git. I test di confronto non autorizzano automaticamente modifiche o stampe sul PC di riferimento.
