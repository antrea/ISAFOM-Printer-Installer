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

`Removal.cs` e `RemovalStorage.cs` gestiscono piano, presentazione e conferma. `Scripts/RemovalBackend.ps1` produce e ricontrolla l'inventario; `DriverLocks.cs.txt` fornisce controlli nativi su file e utilizzatori. Il nome risorsa e `WithCulture=false` di questo file nel progetto sono intenzionali.

Il piano forzato estende transitivamente le dipendenze solo ai modelli Kyocera KX ammissibili. L'impronta confermata è ricontrollata dal processo elevato. Le operazioni sono limitate alle risorse elencate, con controlli su lavori, dipendenze esterne, percorsi e hash e una rilettura finale. Le prove della chiusura delle dipendenze sono in `tests/Test-ForcedRemoval.ps1`.

## Verifiche e limiti

I test separano regole sintetiche, simulazioni della rimozione, layout WPF e letture reali del PC. I comandi nativi che installano o rimuovono stampanti sono separati dai test normali. I risultati e le prove ancora necessarie sono descritti in [VALIDATION.md](../VALIDATION.md).
