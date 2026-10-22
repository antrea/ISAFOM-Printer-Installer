# Guida amministratore

## Configurazione e comportamento

I default corrispondono alla [guida ISAFOM](https://isafom.cnr.it/stampante-taskalfa-3554ci-kx-portici/):

| Parametro | Valore |
| --- | --- |
| Modello/coda | Kyocera TASKalfa 3554ci KX |
| Host e porta logica | `stampante.isafom.cnr.it` |
| Ripiego su errore DNS | `140.164.75.11` |
| Protocollo | RAW, porta 9100 |
| SNMP | Attivo, community `isafom`, indice 1 |
| Condivisione | Disabilitata |
| Driver di avvio | KX 8.5.2405 x64 |
| Driver opzionale | KX 8.7.0917 x64 |
| Preferenze | Job Accounting, ID specifico, stampa privata senza richiesta password, bianco e nero |

Il fallback è esplicito: un host risolto ma non raggiungibile non causa il passaggio silenzioso all'IP. Il programma non modifica deliberatamente la stampante predefinita. La configurazione personale va eseguita dalla sessione dell'utilizzatore.

## Aggiornare i dati personali

Rieseguire l'installazione con Nome e nuovo ID. Se driver, porta e coda sono quelli attesi, vengono riutilizzati. Il codice è una stringa di quattro cifre: `0007` e `7` non sono equivalenti. Non clonare i dati personali del PC di riferimento su altri utenti.

Le preferenze vengono applicate e rilette nel driver, distinguendo coda e profilo corrente. Non si promette l'isolamento dei dati globali fra più utenti della stessa coda: collaudare il caso multiutente prima di distribuire il programma in tale scenario.

## Cambiare host/IP e parametri amministrativi

Partire da [config/printer-settings.json](../config/printer-settings.json). L'override è `%ProgramData%\CNR-ISAFOM\printer-settings.json`, con ACL amministrative. Non inserire campi personali o codici nel JSON.

Da PowerShell 7 elevata:

```powershell
.\build\Set-AdminConfiguration.ps1 -ConfigurationFile .\config\printer-settings.json -Executable .\dist\installer-0.2.7-rc1\Installa_Stampante_ISAFOM.exe
```

Il comando è una modifica amministrativa esplicita: prima convalida il testo tramite l'EXE, poi imposta la configurazione protetta. Il programma rifiuta proprietà sconosciute, file troppo grandi, percorsi o ACL non sicuri. Cambiare il driver richiede una modifica ai profili, alle impronte e al relativo collaudo; non basta cambiare il numero di versione nel JSON.

## Driver 8.5 e 8.7

La 8.5 è il default. Da Debug scegliere la 8.7 e premere **Installa versione selezionata**. Il prossimo avvio torna alla 8.5. Windows 10/11 x64 usano lo stesso payload x64: non sono selezionati pacchetti ARM64/32 bit.

Una registrazione differente produce `DriverConflict`. Il programma non aggiorna alla cieca un driver condiviso. Prima di passare di versione, controllare il piano di rimozione.

## Rimozione completa e forzata

La modalità ordinaria lavora sulla stampante prevista e blocca dipendenze condivise. Quella forzata calcola la chiusura delle dipendenze fra registrazioni **Kyocera KX**, pacchetti OEM, file e code. Può coinvolgere altri modelli; anteprima e conferma mostrano l'ambito effettivo, comprese code condivise e predefinita.

Il piano include porte delle code coinvolte, anche WSD, e porte TCP/IP dell'endpoint ISAFOM rimaste senza coda. Una porta usata da una stampante esterna al gruppo blocca la rimozione. L'helper elevato ricalcola il piano e rifiuta una conferma divenuta obsoleta.

I lavori non sono cancellati. Lavori non enumerabili, risorse di altri produttori e dispositivi PnP esterni restano motivi di blocco. Solo i pacchetti verificati nel piano forzato ricevono `/force` da PnPUtil; non vengono usati `/uninstall` o `/reboot`.

Il riavvio dello Spooler e la chiusura dei componenti dedicati sono ammessi solo dai controlli previsti e con code vuote. I file sono limitati da percorso canonico, hash e verifica dell'assenza di altri utilizzatori. Il sistema viene riletto alla fine: il tool non dichiara completo un risultato con residui.

La pulizia comprende le preferenze della sessione corrente e le risorse globali elencate; non attraversa tutti i profili Windows. Non equivale a una cancellazione forense della cronologia di Windows o di backup esterni.

## Log, segnalazioni e collaudo

Il log principale mostra timestamp ed eventi filtrati. Il file applicativo è `%LocalAppData%\CNR-ISAFOM\installer.log`. Non allegare esportazioni del registro, DEVMODE, PrintTicket grezzi o dump di memoria a issue pubbliche. Vedere [diagnostica](DIAGNOSTICA.md).

Prima di una distribuzione ampia completare [VALIDATION.md](../VALIDATION.md), in particolare rimozione forzata reale con dipendenze condivise, stampa/rilascio, nome sul pannello, contabilità, riavvio, nuova sessione, utente standard e Windows 10. Le evidenze della 0.2.6 non sono attribuite all'EXE 0.2.7.

La licenza Kyocera allegata agli archivi vieta il caricamento pubblico del driver. Il repository e le release pubbliche non contengono né i driver né l'EXE che li incorpora. Consultare [le note di distribuzione](../THIRD_PARTY_NOTICES.md).
