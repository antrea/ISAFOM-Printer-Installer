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

**Rimuovi completamente…** presenta gli elementi della stampante da eliminare. Se sono condivisi con altri modelli, il piano può essere bloccato. **Rimozione forzata…** estende il piano ai modelli e alle code Kyocera KX collegati: può quindi eliminare anche altre stampanti elencate. Farlo solo dopo aver letto l'intero elenco e concordato l'intervento con l'amministratore.

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
