# Guida utente

## Prima di iniziare

Usare Windows 10/11 x64 e collegarsi alla rete dell'Istituto. Richiedere all'amministratore il proprio Account ID di quattro cifre. Il programma non assegna nuovi codici. Occorre poter autorizzare le operazioni amministrative con la finestra UAC di Windows.

L'EXE deve essere fornito attraverso il canale autorizzato dell'amministratore oppure compilato seguendo [BUILD.md](../BUILD.md). Il file ZIP pubblico dei sorgenti non è l'installer.

## Installazione

1. Avviare `Installa_Stampante_ISAFOM.exe` dalla propria sessione Windows, evitando di avviare tutta l'applicazione come un altro utente amministratore.
2. Inserire il **Nome** da applicare al driver, fino a 31 caratteri. Accenti, apostrofi e spazi sono ammessi; gli spazi iniziali e finali vengono eliminati.
3. Inserire l'**Account ID** assegnato, di quattro cifre. Mantenere gli eventuali zeri iniziali. Lo stesso valore viene usato per Job Accounting e password privata.
4. Premere **Installa stampante**. Quando Windows richiede l'autorizzazione amministrativa, confermare la richiesta relativa al programma.
5. Seguire la roadmap **Controlli → Connessione → Driver e coda → Preferenze → Verifica finale** e il log nella parte inferiore della finestra.
6. Leggere l'esito finale. Un errore o una verifica parziale non certificano l'installazione.

Il codice rimane nel campo mascherato durante installazione e verifica. **Mostra 5 s** lo rende leggibile temporaneamente. Chiudendo il programma viene cancellato dal campo. Non inviarlo all'assistenza.

## Stampante già presente

Con la versione corretta del driver, una nuova installazione aggiorna Nome e codice sulla coda individuata senza reinstallare il driver. Non vengono create intenzionalmente copie della stampante. Se è rilevata una versione differente, il programma segnala `DriverConflict`: consultare l'amministratore prima della rimozione.

## Verifica

Lasciare Nome e ID nei campi e premere **Verifica installazione**. Il programma rilegge le impostazioni locali e le confronta con i dati inseriti. Questa verifica non accerta che l'ID sia stato autorizzato dall'amministratore della stampante e non invia documenti al dispositivo.

Per il collaudo fisico, concordare una stampa con l'amministratore: controllare sul pannello il nome del lavoro, il rilascio con il proprio codice, il bianco e nero e l'attribuzione contabile. Questi controlli sono distinti dalla verifica locale.

## Debug e rimozione

La scheda **Debug e verifica** mostra informazioni tecniche, versione selezionata, diagnostica e comandi di rimozione. La versione proposta all'avvio è **8.5.2405**; l'amministratore può selezionare **8.7.0917** per la sessione corrente.

**Rimuovi completamente…** presenta gli elementi della stampante da eliminare. Se sono condivisi con altri modelli, il piano può essere bloccato. **Rimozione forzata selettiva…** mostra le code ISAFOM, comprese copie e installazioni incomplete, e le altre Kyocera locali selezionabili, anche con porta errata o mancante. Ogni voce indica nome, driver, porta, IP/host, stato e se è predefinita. Nessuna voce è selezionata all'apertura; **Aggiorna elenco** rilegge il PC e azzera la selezione. È possibile mostrare anche le altre stampanti; quelle di sistema, non pertinenti, condivise, remote o con identità non verificabile restano protette.

Seleziona una o più code desiderate, premi **Controlla selezione…**, leggi il riepilogo e spunta la conferma esplicita prima di **Rimuovi le code confermate**. Se selezioni la predefinita, spunta anche la conferma dedicata: Windows potrà scegliere un'altra predefinita dopo la cancellazione. Le Kyocera non riconosciute come ISAFOM richiedono un'altra conferma dedicata. Le code escluse restano fuori dall'intervento. Driver, pacchetti, porte e file condivisi o di provenienza non dimostrata vengono conservati. La rimozione selettiva funziona anche senza raggiungere la stampante in rete e mostra un esito per ogni coda: rimossa completamente, parzialmente oppure non rimossa, con eventuali residui. La pulizia selettiva conserva la configurazione e la cache generali del programma.

Sono selezionabili anche stampanti di altre marche che presentano almeno uno dei motivi **Stessa porta della Kyocera**, **Stesso IP/host della Kyocera**, **Driver condiviso con la Kyocera** o **Configurazione incompleta**. I motivi compaiono nella riga e nel riepilogo. Una configurazione incompleta può riguardare anche un'altra stampante: non dimostra da sola che appartenga a ISAFOM. Per queste voci è richiesta la conferma dedicata alle code non riconosciute come ISAFOM. Le altre stampanti sane restano in grigio e non selezionabili. Restano protette le code di sistema, condivise, remote, di altri utenti e quelle con identità non verificabile, anche in presenza di uno dei motivi.

Non vengono cancellati i lavori pendenti. Lavori presenti o non leggibili e dipendenze esterne impediscono la rimozione. Se restano file, pacchetti o porte, il risultato viene indicato come incompleto. Se è tutto assente, compare **Nulla da rimuovere**, senza UAC.

## Problemi frequenti

| Segnalazione | Cosa fare |
| --- | --- |
| Stampante non raggiungibile | Controllare rete dell'Istituto e alimentazione della stampante; comunicare all'assistenza la fase e il codice del log. |
| `DriverConflict` | È presente un'altra versione. L'amministratore deve esaminare il piano di rimozione. |
| UAC annullata | Ripetere l'operazione e autorizzare la richiesta di Windows, se consentito. |
| Verifica parziale o errore | Non considerare conclusa l'installazione; usare la diagnostica e comunicare il codice mostrato. |
| Windows mostra «Inattiva» | Può significare semplicemente che la coda non ha lavori. Se mostra «Offline», controllare con l'amministratore rete, porta e SNMP; lo stato da solo non verifica la configurazione. |

Assistenza: **Andrea Esposito — [andrea.esposito@cnr.it](mailto:andrea.esposito@cnr.it)**. Comunicare versione del tool, versione Windows, fase e codice dell'errore, senza Nome personale, Account ID o password.
