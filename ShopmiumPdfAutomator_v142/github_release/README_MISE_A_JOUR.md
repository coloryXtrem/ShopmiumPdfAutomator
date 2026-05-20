# Instructions — Système de mise à jour automatique

## Configuration initiale (une seule fois)

### 1. Créer un repo GitHub (privé ou public)

### 2. Configurer l'URL dans UpdateService.cs
```csharp
public static string VersionJsonUrl { get; set; } =
    "https://raw.githubusercontent.com/VOTRE_USERNAME/VOTRE_REPO/main/version.json";
```

### 3. Uploader version.json sur GitHub
Placer le fichier `version.json` à la racine du repo GitHub.

---

## Pour chaque nouvelle version (ex: v1.1.0)

### Étape 1 — Modifier la version dans le .csproj
```xml
<Version>1.1.0</Version>
<AssemblyVersion>1.1.0.0</AssemblyVersion>
<FileVersion>1.1.0.0</FileVersion>
```

### Étape 2 — Compiler en Release
```
dotnet publish -c Release -r win-x64 --self-contained true
```
Ou via Visual Studio : Build → Publish

### Étape 3 — Créer un Setup.exe (optionnel)
Utiliser Inno Setup ou NSIS pour créer un installeur.

### Étape 4 — Créer une Release GitHub
1. Aller sur votre repo GitHub
2. Cliquer "Releases" → "Create a new release"
3. Tag : `v1.1.0`
4. Uploader le `ShopmiumPdfAutomator_Setup.exe`
5. Publier

### Étape 5 — Mettre à jour version.json sur GitHub
```json
{
  "version": "1.1.0",
  "url": "https://github.com/VOTRE_USERNAME/VOTRE_REPO/releases/download/v1.1.0/ShopmiumPdfAutomator_Setup.exe",
  "notes": "- Correction TVA Danao\n- Nouveau ticket Drive v2\n- Performance améliorée",
  "required": false
}
```
Committer et pusher ce fichier.

---

## Comportement côté utilisateur

- Au démarrage de l'app, après 2 secondes, une vérification silencieuse est lancée
- Si une nouvelle version existe → popup "Mise à jour disponible"
- L'utilisateur clique "Mettre à jour" → téléchargement automatique + installation
- Si `"required": true` → le bouton "Plus tard" est masqué (mise à jour forcée)
- Si pas de connexion → rien ne se passe (échec silencieux)

---

## Format version.json

| Champ | Type | Description |
|---|---|---|
| `version` | string | Nouvelle version (ex: "1.1.0") |
| `url` | string | URL directe vers le .exe ou .zip |
| `notes` | string | Notes de version (\\n pour saut de ligne) |
| `required` | bool | true = mise à jour obligatoire |
