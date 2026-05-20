# Guide d'installation — Shopmium PDF Automator

## Prérequis

- **Visual Studio 2022** (Community gratuit) : https://visualstudio.microsoft.com/
  - Lors de l'installation, cocher : **.NET Desktop Development**
- **.NET 8 SDK** (inclus avec Visual Studio)

---

## Étapes d'installation

### 1. Ouvrir le projet
Double-cliquer sur **`ShopmiumPdfAutomator.sln`**

### 2. Restaurer les packages NuGet
Visual Studio le fait automatiquement. Sinon :
- Menu **Outils** → **Gestionnaire de packages NuGet** → **Console du gestionnaire de packages**
```
PM> Install-Package Magick.NET-Q16-AnyCPU
PM> Install-Package SkiaSharp
PM> Install-Package SkiaSharp.NativeAssets.Win32
PM> Install-Package HtmlAgilityPack
```

### 3. Placer le fichier PSD
Copier votre `fichier.psd` dans :
```
ShopmiumPdfAutomator\Resources\template.psd
```
(ou le laisser sur le Bureau, l'application le trouvera automatiquement)

### 4. Lancer l'application
Appuyer sur **F5** ou cliquer sur ▶ dans Visual Studio.

---

## Utilisation

### Mode Manuel
1. Saisir le nom du produit, la quantité et le prix
2. Choisir le taux de TVA (détection automatique recommandée)
3. Saisir la date (et l'heure, ou laisser vide = aléatoire)
4. Cliquer **GÉNÉRER LE PDF**
5. Le PDF est sauvegardé sur le Bureau dans `TicketsCarrefour\`

### Mode Automatique
1. Ouvrir `https://app.shopmium.com/fr/favorites`
2. `Ctrl+U` → `Ctrl+A` → `Ctrl+C` → coller dans l'app
3. Sélectionner un produit dans la liste
4. Ouvrir la page produit → `Ctrl+U` → `Ctrl+A` → `Ctrl+C` → coller
5. Cliquer **GÉNÉRER LE PDF**

---

## Packages NuGet utilisés

| Package | Usage |
|---------|-------|
| `Magick.NET-Q16-AnyCPU` | Rasterise le PSD en image haute qualité |
| `SkiaSharp` | Overlay texte + export PDF |
| `HtmlAgilityPack` | Parse le HTML Shopmium |
