# Installer 0.2.7-rc1 — revisione dei sorgenti: rimozione forzata selettiva

In **Debug e verifica** sono disponibili **Rimuovi completamente…** e **Rimozione forzata selettiva…**. La seconda modalità permette di scegliere esplicitamente una o più code ISAFOM, comprese copie e installazioni incomplete, conservando le risorse necessarie alle altre stampanti.

L'inventario è aggiornabile e indica nome, driver, porta, IP/host e stato. Le checkbox sono inizialmente vuote. Stampante predefinita, code condivise/remotamente connesse, stampanti di sistema, non pertinenti e con identità ambigua sono protette. Il GUID di istanza Windows distingue copie e code ricreate con lo stesso nome.

Aprire **Rimozione forzata selettiva…**, selezionare le code, premere **Controlla selezione…**, leggere il riepilogo, spuntare la conferma e premere **Rimuovi le code confermate**. Ogni cambio di selezione o aggiornamento invalida la conferma. L'helper UAC ricalcola il piano prima di modificare il PC.

## Porte e residui

Sono candidabili solo le porte TCP/IP dedicate alle code selezionate e riconducibili all'endpoint ISAFOM. Porte condivise, WSD e porte esterne alla selezione restano conservate. Le dipendenze vengono ricontrollate prima delle operazioni e possono soltanto ridurre la pulizia prevista.

La rimozione delle code non richiede che il driver sia ancora integro. Driver, pacchetti riconosciuti e file noti vengono eliminati solo se è dimostrato che non servono ad altre code, registrazioni o dispositivi. I casi non verificabili restano residui espliciti o risorse conservate. Cache, configurazione e cache delle preferenze nei profili restano fuori dalla pulizia selettiva.

Il percorso selettivo non usa `/force`, reset dello spooler, chiusure di processi o cancellazioni di lavori. Il recupero di una coda usa soltanto la stessa istanza Winspool identificata e verificata.

Lavori pendenti o non verificabili bloccano la coda interessata. Il comando Windows concluso senza errori non è una prova di rimozione: l'assenza viene riletta. Ogni coda riceve un esito individuale, con residui e risorse condivise conservate. Una richiesta di riavvio di Windows lascia l'esito parziale e non provoca un riavvio automatico.

## Installazione e validazione

KX **8.5.2405** resta predefinito; **8.7.0917** è facoltativo nel Debug. Nome, Account ID di quattro cifre, stampa privata e bianco e nero mantengono il comportamento della 0.2.6. Leggere `VALIDATION.md` per distinguere test della rimozione forzata con inventari simulati, anteprima reale in sola lettura e precedente collaudo nativo delle preferenze. Questa revisione non rimuove automaticamente le stampanti del PC di sviluppo durante la compilazione.

Assistenza: **Andrea Esposito — andrea.esposito@cnr.it**, CNR-ISAFOM Portici.
