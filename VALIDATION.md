# Validazione — 0.2.7-rc1

## Risultati già registrati

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

- [ ] Rimozione forzata reale di un gruppo con driver condivisi, su un PC che presenta quella configurazione.
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
