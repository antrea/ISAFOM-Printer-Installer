# Installer 0.2.7-rc1 — revisione dei sorgenti: rimozione forzata selettiva

In **Debug e verifica** sono disponibili **Rimuovi completamente…** e **Rimozione forzata selettiva…**. La seconda modalità permette di scegliere esplicitamente una o più code ISAFOM, comprese copie e installazioni incomplete, conservando le risorse necessarie alle altre stampanti.

L'inventario è aggiornabile e indica nome, driver, porta, IP/host, stato e se la coda è predefinita. Le checkbox sono inizialmente vuote e permettono la selezione multipla. Sono selezionabili anche altre Kyocera locali, comprese quelle con porta errata o mancante, senza confonderle con quelle associate a ISAFOM. Code condivise/remotamente connesse, stampanti di sistema, non pertinenti e con identità ambigua sono protette. Il GUID di istanza Windows distingue copie e code ricreate con lo stesso nome.

Aprire **Rimozione forzata selettiva…**, selezionare le code, premere **Controlla selezione…**, leggere il riepilogo, spuntare la conferma e premere **Rimuovi le code confermate**. Ogni cambio di selezione o aggiornamento invalida la conferma. L'helper UAC ricalcola il piano prima di modificare il PC.

La predefinita può essere rimossa con una conferma dedicata: Windows potrà scegliere un'altra predefinita. Anche le Kyocera non riconosciute come ISAFOM richiedono una conferma dedicata. Ogni conferma parte vuota e riguarda esclusivamente le code nel riepilogo. Porte e risorse di provenienza non dimostrata restano conservate.

Sono ora selezionabili anche code di altre marche con porta, IP/host o driver in comune con ISAFOM/Kyocera e code con configurazione incompleta. Ogni riga e il riepilogo ne indicano i motivi. Le code senza tali criteri restano in grigio. La conferma di appartenenza non verificata si applica a tutte queste code. I collegamenti non comportano preselezioni o estensione del piano ad altre stampanti e non superano le protezioni per sistema, code condivise, remote, altri utenti o identità ambigue.

## Porte e residui

Sono candidabili solo le porte TCP/IP dedicate alle code selezionate e riconducibili all'endpoint ISAFOM. Porte condivise, WSD e porte esterne alla selezione restano conservate. Le dipendenze vengono ricontrollate prima delle operazioni e possono soltanto ridurre la pulizia prevista.

La rimozione delle code non richiede che il driver sia ancora integro. Driver, pacchetti riconosciuti e file noti vengono eliminati solo se è dimostrato che non servono ad altre code, registrazioni o dispositivi. I casi non verificabili restano residui espliciti o risorse conservate. Cache, configurazione e cache delle preferenze nei profili restano fuori dalla pulizia selettiva.

Il percorso selettivo non usa `/force`, reset dello spooler, chiusure di processi o cancellazioni di lavori. Il recupero di una coda usa soltanto la stessa istanza Winspool identificata e verificata.

Lavori pendenti o non verificabili bloccano la coda interessata. Il comando Windows concluso senza errori non è una prova di rimozione: l'assenza viene riletta. Ogni coda riceve un esito individuale, con residui e risorse condivise conservate. Una richiesta di riavvio di Windows lascia l'esito parziale e non provoca un riavvio automatico.

## Installazione e validazione

KX **8.5.2405** resta predefinito; **8.7.0917** è facoltativo nel Debug. Nome, Account ID di quattro cifre, stampa privata e bianco e nero mantengono il comportamento della 0.2.6. Leggere `VALIDATION.md` per distinguere test della rimozione forzata con inventari simulati, anteprima reale in sola lettura e precedente collaudo nativo delle preferenze. Questa revisione non rimuove automaticamente le stampanti del PC di sviluppo durante la compilazione.

Assistenza: **Andrea Esposito — andrea.esposito@cnr.it**, CNR-ISAFOM Portici.
