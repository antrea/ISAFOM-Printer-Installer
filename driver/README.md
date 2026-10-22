# Preparazione dei driver Kyocera

**I binari proprietari non sono pubblicati.** Gli archivi ufficiali includono un accordo di licenza la cui clausola d) vieta di pubblicare il software in una posizione accessibile al pubblico. L'EXE locale incorpora gli stessi driver e non è quindi un asset di questa pubblicazione.

Procurarsi dal produttore o dal canale autorizzato dell'Istituto i due archivi esatti, leggendo le rispettive condizioni. Punti di partenza: [download Kyocera](https://www.kyoceradocumentsolutions.com/download/) e [guida ISAFOM](https://isafom.cnr.it/stampante-taskalfa-3554ci-kx-portici/). Non si assume che il sito del produttore conservi indefinitamente una vecchia versione.

| Versione | Archivio originale | SHA-256 originale |
| --- | --- | --- |
| Default 8.5 | `Kx85_UPD_8.5.2405_en_RC2_WHQL.zip` | `54CFBE11ECBB989566EE8ACCD12144E6267A57C3CFB062BA3E2D8DF711502ADA` |
| Opzionale 8.7 | `Kx87_UPD_8.7.0917_en_RC3_signed.zip` | `E7655FB105B353E7F7F18D6CB88B5CE6CD01FF21E5234471A24DC2B0585DD9A9` |

La 8.7 è il pacchetto RC3 firmato fornito per questo progetto; non viene presentato come WHQL.

```powershell
.\build\Prepare-Kx87.ps1 -Version 8.5.2405 -OfficialArchive 'C:\Driver\Kx85_UPD_8.5.2405_en_RC2_WHQL.zip'
.\build\Prepare-Kx87.ps1 -Version 8.7.0917 -OfficialArchive 'C:\Driver\Kx87_UPD_8.7.0917_en_RC3_signed.zip'
.\build\Test-DriverPayloads.ps1
```

Lo script, nonostante il nome storico, supporta entrambe le versioni. Seleziona x64 e i documenti previsti, non espande ARM64/32 bit e verifica ogni file incluso contro l'originale. Gli archivi risultanti devono avere i seguenti hash:

| Payload locale | SHA-256 |
| --- | --- |
| `KX_8.5.2405_x64.zip` | `7100E66596C06252621B604D6CF19EA30527949D56319D83873909A66FC3092E` |
| `KX_8.7.0917_x64.zip` | `C1ABC58594979E2B38B8344B8AB7DF3DBDFC987DFFDF3FF02BB5F6FC289DCD8F` |

I manifest JSON contengono nomi, dimensioni e impronte dei file, non i file stessi. `KX_SpoolFiles.json` lega i nomi dei componenti alle impronte autorizzate per la pulizia. Non cambiare questi vincoli solo per accettare un pacchetto diverso.

Conservare le licenze originali nel pacchetto locale: sono dentro gli archivi preparati. Prima di distribuire un EXE costruito localmente, verificare i diritti e le condizioni applicabili. Non caricare driver, loro estratti, disassemblati o EXE incorporanti driver nelle release pubbliche.
