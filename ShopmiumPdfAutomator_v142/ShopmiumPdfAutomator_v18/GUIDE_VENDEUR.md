# Guide Vendeur — Shopmium PDF Automator

## 🔑 Étape 1 — Personnaliser ta clé secrète

**OBLIGATOIRE avant de distribuer l'application.**

Génère une clé secrète unique sur : https://generate-secret.now.sh/64

Remplace `REMPLACE_PAR_TA_CLE_SECRETE_64_CHARS_MIN_OBLIGATOIRE` par ta clé dans **ces deux fichiers** :
- `ShopmiumPdfAutomator/Services/LicenseService.cs` → ligne avec `const string SECRET_KEY`
- `GenerateLicense/GenerateLicense.cs` → ligne avec `const string SECRET_KEY`

⚠️ Les deux fichiers doivent avoir **exactement la même clé**.

---

## 📦 Étape 2 — Compiler l'application cliente

Dans Visual Studio :
1. Clic droit sur `ShopmiumPdfAutomator` → **Publier**
2. Cible : **Dossier**
3. Paramètres :
   - Mode de déploiement : **Autonome**
   - Runtime : **win-x64**
   - Fichier unique : ✅
4. Cliquer **Publier**

Le `.exe` est dans `bin/Release/net8.0-windows/win-x64/publish/`

**Livrer à l'acheteur :**
```
📁 ShopmiumPdfAutomator/
├── ShopmiumPdfAutomator.exe
└── Resources/
    └── template.psd
```

---

## 🛠️ Étape 3 — Compiler l'outil vendeur (GenerateLicense)

```bash
cd GenerateLicense
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Garde `GenerateLicense.exe` uniquement pour toi. **Ne jamais le distribuer.**

---

## 🎫 Étape 4 — Générer une clé pour un client

Lance `GenerateLicense.exe` et choisis :
- **1** → Licence permanente (paiement unique)
- **2** → Licence avec expiration (abonnement mensuel/annuel)

Exemples de clés générées :
```
SHPM-ABCD-EFGH-IJKL-MNOP   ← permanente
SHPM-QRST-UVWX-YZ23-4567   ← expire 31/12/2026
```

Envoie la clé au client par email après paiement.

---

## 💰 Modèles de prix suggérés

| Offre | Durée | Prix suggéré |
|---|---|---|
| Licence permanente | Illimitée | 49€ |
| Abonnement annuel | 12 mois | 29€/an |
| Abonnement mensuel | 1 mois | 4,99€/mois |

---

## ❓ FAQ Client

**L'acheteur ne trouve pas sa clé ?**
→ Renvoyer la clé par email. Une clé peut être utilisée sur **1 seul PC**.

**L'acheteur change de PC ?**
→ Générer une nouvelle clé (même expiration que l'originale).

**La licence dit "expirée" ?**
→ Générer une nouvelle clé avec une nouvelle date.
