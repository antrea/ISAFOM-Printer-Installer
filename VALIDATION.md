# Validazione — 0.2.7-rc1

## Selezione per porta, IP, driver o configurazione incompleta — 23 ottobre 2026

Le code di altre marche sono selezionabili quando condividono porta, IP/host o driver con ISAFOM/Kyocera, oppure risultano incomplete. Le altre stampanti sane restano disabilitate. Ogni riga e il riepilogo espongono i motivi; questi non attestano proprietà ISAFOM e non comportano alcuna preselezione. Restano applicate le protezioni di sistema, condivisione, connessione remota, altri utenti e identità. Il piano resta limitato agli ID confermati.

L'[evidenza della revisione](validation-evidence/selective-related-queues-20261023.json) registra 84 controlli Core, 17 Windows/UI/privacy, 78 installer (inclusi 25 selezione/conferma/protocollo), 123 GUI/DPI, 17 identità e 4 catalogo nella build completa con entrambi i payload ufficiali. La suite selettiva finale supera **70 casi** sia con PowerShell 7 sia con Windows PowerShell 5.1. I casi aggiunti coprono altre marche sulla stessa porta o sullo stesso IP, driver condiviso, driver/porta mancanti, registrazioni incomplete, inventari non leggibili, IPv6, porte aggregate, assenza di collegamenti transitivi, motivi vincolati alla conferma e rimozione di una coda correlata anche dopo la Kyocera di riferimento. Un cambio di IP durante il batch blocca la coda interessata; l'endpoint è ricontrollato anche con l'handle nativo aperto. Le prove rimangono simulate per le cancellazioni: nessuna stampante reale è stata rimossa. L'inventario incorporato reale viene verificato senza cambiare il sistema.

## Rimozione della predefinita e di più Kyocera — 23 ottobre 2026

La rimozione selettiva consente ora di spuntare anche la predefinita e più Kyocera locali con porta errata o mancante. Le code non riconosciute come ISAFOM sono indicate senza attribuire loro una proprietà non verificata. Predefinita e associazione non verificata richiedono conferme dedicate, invalidate insieme al riepilogo quando cambia la selezione. La predefinita viene rimossa per ultima, senza impostarne una sostitutiva. Identità, lavori e dipendenze restano ricontrollati prima delle operazioni.

L'[evidenza di questa revisione](validation-evidence/selective-default-batch-20261023.json) registra la build completa con entrambi i payload ufficiali verificati: 84 controlli Core, 17 Windows/UI/privacy, 76 installer (inclusi 23 di selezione/conferma/protocollo), 123 GUI/DPI, 17 identità e 4 catalogo. I **51 test selettivi** passano con PowerShell 7 e Windows PowerShell 5.1; i 31 test ordinari passano nella build completa. Le regressioni includono due Kyocera configurate male su cinque, rimozione della predefinita selezionata per ultima, riassegnazione automatica simulata di Windows e protezione delle tre code escluse. L'inventario incorporato reale mantiene invariati code, porte e predefinite del profilo e della sessione desktop remoto. La cancellazione nativa su stampanti reali resta da collaudare; i mutatori dei test sono simulati.

## Audit della revisione selettiva — 23 ottobre 2026

Il codice attuale sostituisce la precedente espansione transitiva della rimozione forzata con la selezione esplicita delle istanze Windows. L'[evidenza filtrata](validation-evidence/selective-removal-20261023.json) riguarda questi sorgenti, compilati con SDK 10.0.402 senza archivi proprietari. Il manifest delle risorse identifica i file della revisione; gli hash degli EXE storici riportati sotto non identificano questa build di test.

Eseguito `build/Test-SourceAudit.ps1`: compilazione senza avvisi/errori, 84 controlli Core, 17 Windows/UI/privacy, 53 installer (inclusi 10 controlli della selezione/conferma e del protocollo), 122 GUI/DPI e 17 identità/piattaforma. I 31 controlli di rimozione ordinaria e i **39 di rimozione selettiva** passano sia con PowerShell 7 sia con Windows PowerShell 5.1. L'inventario selettivo reale del backend incorporato è stato letto e confrontato senza cambiare code, porte, driver o predefinita.

Le regressioni dimostrano, con mutatori Windows simulati, la rimozione di **due code su cinque** mantenendo identiche le altre tre e conservandone driver e porte. Coprono copie/identità duplicate, GUID mancanti o riutilizzo del nome, coda offline o incompleta, lavori sopraggiunti, cambio della predefinita, porte aggregate e riferimenti con diversa capitalizzazione, pacchetti/file condivisi con altri modelli, dispositivi PnP esterni, errori intermedi, permessi insufficienti, inventario corrotto, file bloccati, richiesta di riavvio, verifica dell'assenza e ripetizione senza ulteriori modifiche. L'interoperabilità nativa di recupero viene compilata senza chiamare API distruttive.

Non sono stati installati/rimossi dispositivi reali né inviati lavori di stampa. Restano separati il collaudo nativo distruttivo in laboratorio e la build/pubblicazione con payload ufficiali, inclusi i relativi controlli di integrità/catalogo. `Build-Installer.ps1` conserva questi controlli e include ora la regressione dell'inventario selettivo reale.

## Risultati storici dell'EXE 0.2.7-rc1 precedente alla revisione selettiva

La [sintesi originale filtrata](validation-evidence/0.2.7-rc1.json) identifica l'EXE locale tramite SHA-256 **B139E8709863225961807A49024F5F54DE262C363A2ECDCF647227219325B7C1**. Il binario non è distribuito pubblicamente perché contiene driver Kyocera. Una compilazione locale può avere un hash diverso e deve essere verificata come tale.

| Gruppo | Controlli superati | Natura |
| --- | ---: | --- |
| Core | 84 | Regole e simulazioni |
| Windows/WPF/privacy | 17 | Controlli Windows e UI; casi condizionali secondo la coda presente |
| Installer | 56 | Regressioni e comportamenti locali |
| GUI/DPI | 122 | Geometria, campi e stati WPF |
| Rimozione ordinaria | 31 | Inventari simulati |
| Rimozione forzata | 16 | Inventari simulati, anche Windows PowerShell 5.1 |
| Avvio EXE e backend | 16 | Binario pubblicato localmente, anteprime reali senza modifiche |
| Identità/piattaforma | 17 | Controlli di identità e compatibilità |
| Catalogo | 4 | Fiducia e impronte |

Le anteprime reale ordinaria e forzata della 0.2.7 hanno lasciato invariate code, stampante predefinita e porte. L'icona incorporata è stata controllata. Le simulazioni coprono dipendenze transitive fra modelli KX, code condivise, porte TCP/IP orfane, WSD, lavori sopraggiunti, risorse estranee e dipendenze PnP esterne. La rimozione forzata del gruppo condiviso non è stata eseguita realmente in questa revisione.

La precedente **0.2.6-rc1**, hash **BB34BFF969D346EEE7FC1C4C451429471055D37D8B63B6B07087A67AEB29A863**, ha superato il ciclo nativo di applicazione e rilettura dei campi e la rimozione ordinaria completa per 8.5 e 8.7. Era **un altro eseguibile**: queste prove non vengono attribuite alla 0.2.7.

## Da completare sul campo

- [ ] Rimozione selettiva reale di due code su cinque in un laboratorio con driver/porte condivisi, verificando le tre escluse e la predefinita.
- [ ] Stampa e rilascio fisico con PIN.
- [ ] Nome sul pannello e attribuzione contabile sul dispositivo.
- [ ] Persistenza dopo riavvio e nuova sessione.
- [ ] Esecuzione e preferenze con un altro utente standard.
- [ ] Prova reale su Windows 10 x64.

Nessun documento è stato inviato in stampa dai collaudi descritti. La release resta candidata e non soddisfa ancora tutti i criteri di accettazione sul campo.

## Ripetere i controlli

Seguire [BUILD.md](BUILD.md). La build normale esegue regressioni, simulazioni e test della GUI. `tests/Test-PublishedApp.ps1` avvia il proprio EXE con opzioni di lettura/preview: non esegue installazione, rimozione o stampa. Alcuni altri percorsi dei test nativi sono esplicitamente mutativi e non vanno avviati indiscriminatamente. I test storici che richiedono dossier di raccolta privata non sono eseguibili con questo repository pubblico.

Per un nuovo collaudo conservare versione, hash del binario, sistema operativo, profilo utente, driver e risultati, senza esportare i valori personali. Una semplice coda presente o una finestra del driver aperta non bastano a certificare stampa privata e contabilità.

## Verifica dei sorgenti pubblici (22 ottobre 2026)

La [verifica della cartella pubblica](validation-evidence/publication-20261022.json) ha ricompilato l'installer e tutti e quattro gli output degli strumenti ausiliari con SDK/cache esistenti. Superati test Core, Windows/UI/privacy, installer, layout GUI/DPI, simulazioni della rimozione e 16 controlli dell'EXE appena costruito. Il nuovo hash locale è indicato nel documento: non è il binario di riferimento precedente. Gli strumenti ausiliari sono stati compilati senza avviarli. Nessuna installazione/rimozione reale o stampa è stata eseguita in questa verifica.
