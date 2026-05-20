# Créer l'installeur Windows — Guide complet

## Problème fréquent : DLL manquantes
Si l'application ne se lance pas après installation, c'est que tu as utilisé
"Fichier unique" (PublishSingleFile=true) dans l'installeur.
→ Il faut publier en mode DOSSIER pour que les DLL soient copiées.

## Solution : utiliser BUILD.ps1

### Étape 1 — Installer NSIS
Télécharger : https://nsis.sourceforge.io/Download
Installer NSIS (gratuit, open-source)

### Étape 2 — Copier le template.psd
Copie ton fichier `fichier.psd` (ou `template.psd`) dans :
```
ShopmiumPdfAutomator\Resources\template.psd
```

### Étape 3 — Lancer BUILD.ps1
Clic droit sur `BUILD.ps1` → "Exécuter avec PowerShell"

Le script fait automatiquement :
1. Compile l'application (mode dossier, toutes les DLL incluses)
2. Copie TOUS les fichiers dans Setup\files\
3. Lance NSIS pour créer l'installeur

### Résultat
```
Setup\ShopmiumPdfAutomator_Setup_v1.0.0.exe  ← à distribuer
```

## Ce que l'installeur copie sur le PC de l'acheteur
```
C:\Program Files\Shopmium PDF Automator\
├── ShopmiumPdfAutomator.exe    ← application principale
├── Uninstall.exe
├── Resources\
│   └── template.psd
├── *.dll                        ← toutes les DLL .NET
└── [autres fichiers runtime]
```

## Pourquoi mode DOSSIER et pas FICHIER UNIQUE ?
- "Fichier unique" : 1 seul .exe, mais les DLL sont extraites dans %TEMP% à chaque lancement → lent, problèmes avec certains antivirus
- "Dossier" : plusieurs fichiers, mais lancement immédiat, compatible avec tous les antivirus, plus stable
