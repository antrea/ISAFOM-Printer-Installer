# Installer 0.2.7-rc1 — rimozione forzata e porte

In **Debug e verifica** sono disponibili **Rimuovi completamente…** e **Rimozione forzata…**. La seconda modalità risolve il blocco dei driver Kyocera che condividono file con quelli ISAFOM, per esempio TASKalfa 5052ci e 4052ci insieme alla versione 8.2 del 3554ci.

La modalità forzata ricostruisce il gruppo di dipendenze prima della conferma: registrazioni dei modelli Kyocera KX collegati, pacchetti OEM corrispondenti, tutte le code che li utilizzano, porte e file locali. Il gruppo può estendersi a ulteriori modelli Kyocera se condividono a loro volta gli stessi componenti. Le code condivise e l'eventuale stampante predefinita sono indicate nell'elenco. Eliminare quelle code ne rimuove anche le impostazioni globali e può interrompere l'uso da parte di altri utenti; vengono pulite le preferenze corrispondenti nel profilo che avvia il tool.

Aprire **Rimozione forzata…**, controllare l'elenco, spuntare la conferma e premere **Rimuovi forzatamente**. Si può passare fra modalità ordinaria e forzata nella stessa finestra; ogni cambio annulla la precedente spunta di conferma. La richiesta UAC di Windows resta necessaria per le modifiche amministrative.

## Porte e residui

Sono comprese le porte delle code coinvolte, anche WSD, e tutte le porte TCP/IP riferite all'host o all'IP ISAFOM, inclusi gli alias rimasti senza coda. L'anteprima elenca ciascuna porta. Prima di cancellarla il tool ricontrolla configurazione e utilizzo; alla fine ne rilegge l'assenza. Una porta ancora utilizzata da una stampante esterna al gruppo impedisce la rimozione completa e viene segnalata.

Se non ci sono driver registrati né pacchetti da rimuovere, il tool pulisce gli eventuali residui senza un'ulteriore finestra di conferma dell'applicazione. Se non resta alcun elemento, mostra **Nulla da rimuovere** e termina senza UAC. La presenza di altri driver nel gruppo forzato richiede comunque la conferma dell'elenco.

La rimozione conserva i controlli della 0.2.6 su percorsi, hash, processi dedicati Kyocera e rilettura finale. In modalità forzata PnPUtil riceve `/force` soltanto per i pacchetti dell'elenco approvato, dopo i controlli delle dipendenze. Non vengono inviati `/uninstall` o `/reboot`. Il significato dei parametri è nella [documentazione Microsoft](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax#delete-driver).

Lavori pendenti o non enumerabili, risorse di altri produttori e dispositivi fisici non compresi nel piano restano motivi di blocco. Il tool non elimina lavori di stampa. Se il sistema lascia un pacchetto, un file o una porta, l'esito resta incompleto; l'eventuale richiesta di riavvio viene mantenuta. Prima della modifica viene ricostruito il piano amministrativo: un elenco cambiato invalida la conferma.

## Installazione e validazione

KX **8.5.2405** resta predefinito; **8.7.0917** è facoltativo nel Debug. Nome, Account ID di quattro cifre, stampa privata e bianco e nero mantengono il comportamento della 0.2.6. Leggere `VALIDATION.md` per distinguere test della rimozione forzata con inventari simulati, anteprima reale in sola lettura e precedente collaudo nativo delle preferenze. Questa revisione non rimuove automaticamente le stampanti del PC di sviluppo durante la compilazione.

Assistenza: **Andrea Esposito — andrea.esposito@cnr.it**, CNR-ISAFOM Portici.
