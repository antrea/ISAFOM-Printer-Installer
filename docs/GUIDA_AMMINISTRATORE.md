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

La modalità ordinaria lavora sulla stampante prevista e blocca dipendenze condivise. Quella forzata è **selettiva**: checkbox inizialmente vuote, inventario aggiornabile e conferma di un piano con le sole code selezionate. Le dipendenze condivise sono conservate e non estendono il piano ad altre code o modelli. Stampante predefinita, code condivise/remotamente connesse, stampanti di sistema e non pertinenti restano protette.

Il GUID Windows `QueueInstanceId`, insieme alla configurazione, distingue ogni istanza anche in presenza di copie o nomi riutilizzati. Senza un'identità univoca l'applicazione protegge la voce. Le registrazioni incomplete identificabili vengono mostrate; il backend usa handle Winspool e un recupero limitato alla stessa istanza. L'helper elevato ricalcola il piano e rifiuta una conferma divenuta obsoleta.

Si eliminano soltanto porte TCP/IP delle code selezionate riconducibili all'endpoint ISAFOM, inutilizzate e ancora identiche al piano. WSD, altre porte e porte orfane esterne alla selezione vengono conservate. I lavori non vengono cancellati; se presenti o non verificabili impediscono la rimozione della coda interessata. Il driver viene conservato quando usato da altre code; pacchetti e file richiedono anche controlli su registrazioni, dispositivi PnP, percorsi e hash noti. PnPUtil non riceve `/force`, `/uninstall` o `/reboot`.

La rimozione selettiva non riavvia lo Spooler e non chiude componenti del driver. Un file bloccato resta un residuo esplicito. Ogni mutazione richiede una rilettura: l'esito positivo di un comando non prova l'assenza della risorsa. Il risultato individuale distingue rimozione completa, parziale e non verificata/non eseguita. Un errore su una coda non autorizza interventi su altre code non selezionate.

Il ripristino ordinario mantiene il proprio percorso di pulizia e la conferma per eventuali recuperi condizionati. La modalità selettiva conserva cache, configurazione generale del tool e cache delle preferenze nei profili utente; rimuovere una coda elimina i dati che Windows conserva nella coda stessa. Non si attraversano altri profili e non si promette una cancellazione forense della cronologia o dei backup.

## Log, segnalazioni e collaudo

Il log principale mostra timestamp ed eventi filtrati. Il file applicativo è `%LocalAppData%\CNR-ISAFOM\installer.log`. Non allegare esportazioni del registro, DEVMODE, PrintTicket grezzi o dump di memoria a issue pubbliche. Vedere [diagnostica](DIAGNOSTICA.md).

Prima di una distribuzione ampia completare [VALIDATION.md](../VALIDATION.md), in particolare rimozione forzata reale con dipendenze condivise, stampa/rilascio, nome sul pannello, contabilità, riavvio, nuova sessione, utente standard e Windows 10. Le evidenze della 0.2.6 non sono attribuite all'EXE 0.2.7.

La licenza Kyocera allegata agli archivi vieta il caricamento pubblico del driver. Il repository e le release pubbliche non contengono né i driver né l'EXE che li incorpora. Consultare [le note di distribuzione](../THIRD_PARTY_NOTICES.md).
