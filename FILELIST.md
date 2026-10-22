# Mappa delle risorse

| Percorso | Contenuto |
| --- | --- |
| `src/Isafom.Windows` | Installer WPF, log, roadmap, adattatore KX, rimozione, icone, backend incorporati |
| `src/Isafom.Core` | Configurazione, profili driver, pianificazione e verifica |
| `src/Isafom.Collector` | Raccoglitore GUI di impostazioni e confronti |
| `src/Isafom.ConfigExtractor` | Estrattore, verificatore e osservazioni con sentinelle |
| `src/Isafom.KxSentinel` | Prototipo sperimentale di scrittura su coda di laboratorio |
| `tests` | Test automatici e template per il collaudo manuale |
| `diagnostics` | Script autonomi di lettura e confronto |
| `tools` | Wrapper e script di laboratorio, con nomi di output storici |
| `build` | Compilazione, preparazione payload, configurazione amministrativa e icona |
| `config` | Parametri amministrativi, senza dati personali |
| `driver` | Manifest, impronte, nomi dei componenti e istruzioni; nessun driver binario |
| `research/reverse-kx85` | Codec e script originali di analisi, senza librerie del fornitore o disassemblati |
| `docs` | Guide italiane, architettura, note versione, screenshot e avvisi .NET |
| `validation-evidence` | Sintesi dei risultati senza raccolte dei PC |
| `resources/RESOURCE_MANIFEST.json` | SHA-256 dei file pubblicati, escluso il manifest stesso |

Sono esclusi gli archivi dei PC di riferimento, le conversazioni/promemoria operativi, gli output intermedi, i binari proprietari, gli SDK e le cache. Il repository contiene i sorgenti necessari alla build corrente e quelli dei tool di raccolta; gli script di packaging interni legati ai vecchi dossier locali non fanno parte del flusso pubblico.
