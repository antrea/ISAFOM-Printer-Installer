# Ricerca di interoperabilità KX

Questa cartella conserva il codice originale del codec e gli script di analisi. Non contiene librerie Kyocera, immagini derivate, disassemblati, chiavi estratte, dati dei PC o ambienti di analisi installati.

Il codec C# è incluso dai progetti dell'installer e dei tool e riconosce i file originali ammessi tramite SHA-256. `codec/CodecProof.csproj` è un programma di confronto per buffer sintetici; il suo avvio non equivale a una prova con stampante fisica.

Gli script Python sono strumenti di laboratorio opzionali. Richiedono i file originali ottenuti legittimamente e, secondo lo script, `pefile`, `capstone`, `unicorn`; usare un ambiente dedicato nella cartella locale, senza copiarlo in Git. I percorsi predefiniti si riferiscono alla struttura di laboratorio storica `research/expanded`. Configurare i propri percorsi e ispezionare gli argomenti dello script prima dell'esecuzione. I risultati sono esclusi da Git.

`rea_native_session.py` usa [REA](https://github.com/morluto/rea). Richiede Node nel PATH e `ISAFOM_REA_ENTRY` con il percorso di `scripts/rea.mjs`. Ghidra/JDK, se necessari alla propria installazione REA, si configurano tramite le normali variabili d'ambiente. Non avviare installazioni automatiche di ambienti pesanti per compilare il tool: l'installer non dipende da REA.

Gli script di emulazione del salvataggio contengono fixture virtuali per i confini di sistema. Non sono una prova che tutte le API Windows o i comportamenti fisici della stampante siano emulati. Non pubblicare i binari derivati prodotti dall'analisi.
