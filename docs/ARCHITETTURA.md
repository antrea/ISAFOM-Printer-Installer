# Architettura della versione 0.2.7-rc1

## Flusso

`WPF nella sessione utente → validazione Nome/ID → inventario → rete → helper UAC → driver/porta/coda → preferenze KX → rilettura → esito`.

`Isafom.Core` gestisce configurazione, pianificazione, validazione, profili driver ed esiti. `Isafom.Windows` contiene GUI WPF, layout adattivo, roadmap, log, elevazione e backend PowerShell incorporati. Il processo principale resta nella sessione dell'utilizzatore; l'helper elevato esegue le operazioni amministrative.

## Configurazione KX

`Kx85Preferences.cs` usa l'adattatore nativo per le versioni esatte ammesse. I file `NativeTarget`, `NativeWorker` e `PrinterStore` sono condivisi con il prototipo sentinella; il codec in `research/reverse-kx85/codec` è incluso dal progetto. Il codec legge dati necessari dal file originale con hash riconosciuto: il repository non incorpora chiavi estratte o copie dei binari Kyocera.

Una scrittura conclusa senza errore non basta. La verifica rilegge Nome, flag e codici e restituisce confronti, evitando di serializzare i valori personali. I profili di firma, hash e struttura sono legati a KX 8.5.2405 e 8.7.0917: una futura versione necessita analisi e collaudo specifici.

## Driver e privilegi

Il programma finale incorpora i due payload x64 costruiti localmente dagli archivi ufficiali. Prima dell'installazione verifica integrità, INF e catalogo. Gli archivi non sono scaricati durante l'avvio della GUI. Configurazione amministrativa e cache hanno ACL controllate; i percorsi vengono verificati per evitare attraversamenti o collegamenti non sicuri.

La comunicazione con i processi di lavoro usa canali privati; Nome e Account ID non vanno sulla riga di comando, nei log o in temporanei di testo. Il driver conserva i dati secondo i propri meccanismi: non si promette un archivio segreto o un isolamento multiutente non dimostrato.

## Rimozione

`Removal.cs` e `RemovalStorage.cs` gestiscono il ripristino ordinario. `SelectiveRemoval.cs` e `Scripts/SelectiveRemoval.ps1` implementano la rimozione forzata selettiva: inventario aggiornabile, checkbox vuote, riepilogo separato e conferma esplicita. `DriverLocks.cs.txt` fornisce controlli nativi su file e utilizzatori. Il nome risorsa e `WithCulture=false` di questo file nel progetto sono intenzionali.

Il piano selettivo contiene soltanto i GUID `QueueInstanceId` scelti dall'utente. L'helper autenticato ricostruisce e confronta l'impronta del piano; un handle Winspool identifica la singola istanza durante la cancellazione. Nome, GUID, driver, porta, commento, lavori e protezioni sono ricontrollati. Identità ambigue o mancanti, code condivise, remote, di sistema e non pertinenti sono protette. Le code Kyocera locali sono selezionabili anche con porta errata o mancante, senza attribuirle automaticamente a ISAFOM: l'associazione non verificata richiede una conferma dedicata. Una predefinita selezionata richiede un'altra conferma dedicata; il suo stato è vincolato all'impronta e ricontrollato anche mentre è aperto l'handle. Un cambiamento dopo il riepilogo richiede una nuova revisione. L'applicazione non imposta una predefinita sostitutiva; Windows può sceglierla dopo la rimozione. Le registrazioni incomplete ancora identificabili nel registro vengono mostrate e possono ricevere un tentativo nativo mirato.

Sono selezionabili anche code di altre marche con la stessa porta, IP/host o nome del driver di una registrazione ISAFOM/Kyocera. I riferimenti includono porta, host, IP di fallback e driver configurati dell'installer. Il confronto è locale, senza DNS o prove di raggiungibilità; gestisce porte aggregate, maiuscole/minuscole e IPv6 equivalenti. I collegamenti non sono transitivi attraverso le nuove code correlate. Una configurazione è incompleta quando mancano driver o porta, la registrazione del driver o della porta risulta assente, è presente il marcatore pending oppure la coda compare soltanto nel registro. Un inventario non leggibile non dimostra una configurazione incompleta. `Reasons` espone i motivi senza attribuire proprietà ISAFOM e li vincola all'impronta del riepilogo. Le stampanti sane senza questi criteri restano disabilitate. Le protezioni di sistema, condivisione, connessione remota, proprietà di altri utenti e identità prevalgono sui motivi di selezionabilità.

Il piano viene rivalidato prima di ogni intervento. Nel corso di un batch già approvato la scomparsa della coda Kyocera di riferimento non impedisce la rimozione di un'altra istanza selezionata con firma immutata: sono ancora applicate tutte le protezioni specifiche della coda. Non sono mai aggiunte altre istanze al piano. La pulizia delle risorse conserva i controlli di provenienza e uso condiviso anche per code di altre marche.

Le protezioni di sistema riconoscono i driver dei componenti Windows (Print to PDF, XPS, Fax, OneNote e reindirizzamento remoto). Il produttore Microsoft da solo non identifica una stampante di sistema: una coda fisica con Microsoft IPP Class Driver resta selezionabile quando soddisfa i criteri. Analogamente una porta di stampa su file non rende di sistema una coda Kyocera installata per errore su quella porta; la porta stessa resta esclusa dalla pulizia TCP/IP.

Le dipendenze riducono le risorse rimovibili: non aggiungono mai altre code o altri modelli al piano. Porte dedicate, registrazioni, pacchetti INF riconosciuti e file con hash noti vengono rimossi solo dopo nuove verifiche degli utilizzatori; risorse condivise o non verificabili rimangono. Non sono previsti reset dello spooler, chiusure di processi, cancellazioni di lavori, `/force`, recuperi globali dei file o pulizia della configurazione/cache del tool nel percorso selettivo. Ogni coda riceve un esito verificato con residui e risorse conservate. Le regressioni, incluso il caso due code su cinque, sono in `tests/Test-ForcedRemoval.ps1` e `SelectiveRemovalChecks.cs`.

## Verifiche e limiti

I test separano regole sintetiche, simulazioni della rimozione, layout WPF e letture reali del PC. I comandi nativi che installano o rimuovono stampanti sono separati dai test normali. I risultati e le prove ancora necessarie sono descritti in [VALIDATION.md](../VALIDATION.md).
