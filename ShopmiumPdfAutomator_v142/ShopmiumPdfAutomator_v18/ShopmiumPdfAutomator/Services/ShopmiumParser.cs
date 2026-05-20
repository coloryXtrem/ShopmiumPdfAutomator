using HtmlAgilityPack;
using ShopmiumPdfAutomator.Models;
using System.Net;
using System.Text.RegularExpressions;
using System.Linq;

namespace ShopmiumPdfAutomator.Services
{
    public static class ShopmiumParser
    {
        // ══════════════════════════════════════════════════════════════════════
        //  PAGE FAVORIS
        // ══════════════════════════════════════════════════════════════════════
        public record FavoriteItem(string Name, string Url);

        public static List<FavoriteItem> ParseFavorites(string html)
        {
            var doc   = new HtmlDocument();
            doc.LoadHtml(html);
            var items = new List<FavoriteItem>();

            var nodes = doc.DocumentNode
                .SelectNodes("//li[contains(@class,'nodesListItem')]//a[@class='node']");
            if (nodes == null) return items;

            foreach (var node in nodes)
            {
                var href = node.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(href)) continue;
                var h3   = node.SelectSingleNode(".//h3");
                var name = WebUtility.HtmlDecode(h3?.InnerText.Trim() ?? "Produit inconnu");
                if (!href.StartsWith("http"))
                    href = "https://offers.shopmium.com" + href;
                if (!items.Any(i => i.Url == href))
                    items.Add(new FavoriteItem(name, href));
            }
            return items;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PAGE PRODUIT
        // ══════════════════════════════════════════════════════════════════════
        public static ProductData ParseProduct(string html)
        {
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);
            var data = new ProductData();

            // ── Titre (pour usage interne uniquement — le nom final vient des références éligibles)
            var h1 = doc.DocumentNode
                .SelectSingleNode("//h1[contains(@class,'heading-block-title')]");
            string h1Title = h1 != null ? WebUtility.HtmlDecode(h1.InnerText.Trim()) : string.Empty;

            // ── Nombre d'articles max ─────────────────────────────────────────
            var articleMatches = Regex.Matches(html,
                @"(\d+)\s+articles?\s+achet[ée]s?\s*=\s*-(\d+)%",
                RegexOptions.IgnoreCase);
            if (articleMatches.Count > 0)
                data.MaxArticles = articleMatches
                    .Max(m => int.Parse(m.Groups[1].Value));

            // ── Prix et nom produit le plus cher ──────────────────────────────
            // Format 1 : "- Malesan Rouge 75cL (4,70€)"
            var priceMatches = Regex.Matches(html,
                @"[\-\u2013]\s+([^(]+?)\s*\((\d+[,.]\d+)\u20AC\)",
                RegexOptions.IgnoreCase);

            // Format 2 : "SUR 1 À 3 ARTICLES 4,42€ à 4,70€*" (bloc heading-block-discount)
            // → prendre le prix le plus élevé (celui après "à")
            var rangeMatch = Regex.Match(html,
                @"(\d+[,.]\d+)\s*\u20AC\s*[\u00e0a]\s*(\d+[,.]\d+)\s*\u20AC",
                RegexOptions.IgnoreCase);

            // Format 3 : "4,42€ - 4,70€" dans Offers-RebateLine--strikethrough
            var strikethroughMatch = Regex.Match(html,
                @"(\d+[,.]\d+)\u20AC\s*-\s*(\d+[,.]\d+)\u20AC",
                RegexOptions.IgnoreCase);

            if (priceMatches.Count > 0)
            {
                var prices = priceMatches
                    .Select(m => new
                    {
                        Name  = m.Groups[1].Value.Trim(),
                        Price = double.Parse(
                            m.Groups[2].Value.Replace(',', '.'),
                            System.Globalization.CultureInfo.InvariantCulture)
                    })
                    .OrderByDescending(p => p.Price).ToList();
                data.MaxPrice    = prices[0].Price;
                data.MinPrice    = prices[^1].Price;
                // ProductName sera écrasé par ExtractEligibleReferences — ne pas assigner ici
            }
            else if (rangeMatch.Success)
            {
                // "X,XX€ à Y,YY€" → prendre Y,YY (le plus élevé)
                var p1 = double.Parse(rangeMatch.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture);
                var p2 = double.Parse(rangeMatch.Groups[2].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture);
                data.MaxPrice = Math.Max(p1, p2);
                data.MinPrice = Math.Min(p1, p2);
            }
            else if (strikethroughMatch.Success)
            {
                // "X,XX€ - Y,YY€" → prendre le plus élevé
                var p1 = double.Parse(strikethroughMatch.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture);
                var p2 = double.Parse(strikethroughMatch.Groups[2].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture);
                data.MaxPrice = Math.Max(p1, p2);
                data.MinPrice = Math.Min(p1, p2);
            }

            // ── Nom depuis "Référence(s) éligible(s)" — SOURCE UNIQUE ET OBLIGATOIRE
            ExtractEligibleReferences(html, data);
            // Fallback uniquement si les références n'ont rien donné
            var priceNameFallback = priceMatches.Count > 0
                ? priceMatches.Select(m => new { Name = WebUtility.HtmlDecode(m.Groups[1].Value.Trim()),
                    Price = double.Parse(m.Groups[2].Value.Replace(',', '.'),
                        System.Globalization.CultureInfo.InvariantCulture) })
                    .OrderByDescending(p => p.Price).First().Name
                : string.Empty;
            ApplyProductNameFallback(data, priceNameFallback, h1Title);

            // ── Date de début ─────────────────────────────────────────────────
            var datePatterns = new[]
            {
                @"[Vv]alable\s+(?:entre\s+le|du)\s+(\d{2}/\d{2}/\d{4})",
                @"(\d{2}/\d{2}/\d{4})\s+[\u00e0a]\s+partir",
            };
            foreach (var p in datePatterns)
            {
                var m = Regex.Match(html, p);
                if (m.Success) { data.StartDate = m.Groups[1].Value; break; }
            }
            if (string.IsNullOrEmpty(data.StartDate))
                data.StartDate = DateTime.Now.ToString("dd/MM/yyyy");

            // ── TVA ──────────────────────────────────────────────────────────
            // Source principale : texte "Au rayon XXX" dans le HTML Shopmium
            // → correspond au rayon physique GMS → taux TVA certain et fiable
            var rayonText = TvaCalculator.ExtractRayonFromHtml(html);
            var tvaResult = TvaCalculator.DetectWithConfidence(data.ProductName, rayonText);
            data.TvaRate              = tvaResult.Rate;
            data.TvaNeedsConfirmation = !tvaResult.Certain;
            data.RayonText            = rayonText; // conservé pour affichage dans l'UI

            // ── Conditions de preuve ──────────────────────────────────────────
            data.RawConditions      = ExtractConditionsText(doc);
            data.ProofRequirements  = DetectAllRequirements(data.RawConditions);

            // ── Offre exclusive à l'application ──────────────────────────────
            // Détecter <h4 class="details-app-only"> ou texte "exclusivement sur l'application"
            var appOnlyNode = doc.DocumentNode.SelectSingleNode(
                "//*[contains(@class,'details-app-only')]");
            if (appOnlyNode != null)
            {
                data.IsAppExclusive = true;
            }
            else
            {
                // Fallback : recherche textuelle dans le HTML brut
                data.IsAppExclusive = Regex.IsMatch(html,
                    @"exclusivement\s+sur\s+l.{1,5}application\s+Shopmium",
                    RegexOptions.IgnoreCase);
            }

            // ── Marque du produit (pour enrichir la requête de recherche) ──────
            data.Brand = ExtractBrand(html);

            // ── Type de ticket à utiliser (détecté via warning popup) ────────
            (data.TicketType, data.WarningText) = DetectTicketType(html);

            return data;
        }

        // ── Extraction de la marque ───────────────────────────────────────────
        private static string? ExtractBrand(string html)
        {
            // Source 1 : dataLayer "product_brand":"MARQUE" (le plus fiable)
            var m1 = Regex.Match(html,
                @"""product_brand""\s*:\s*""([^""]{1,60})""",
                RegexOptions.IgnoreCase);
            if (m1.Success)
            {
                var brand = m1.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(brand) && brand != "shopmium")
                    return brand;
            }

            // Source 2 : breadcrumb 2e niveau (ex: "Malesan, des vins qui se distinguent")
            var m2 = Regex.Match(html,
                @"shpm-breadcrumb[\s\S]{0,200}<span>\s*<a[^>]*>([^<]{2,60})</a>\s*</span>\s*<span>",
                RegexOptions.IgnoreCase);
            if (m2.Success)
            {
                // Prendre uniquement la partie avant la virgule si présent
                var raw = m2.Groups[1].Value.Trim();
                var comma = raw.IndexOf(',');
                return comma > 1 ? raw[..comma].Trim() : raw;
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DÉTECTION TYPE DE TICKET
        // ══════════════════════════════════════════════════════════════════════

        // Sous-enseignes Carrefour exclues qui NE bloquent PAS Carrefour Market
        private static readonly string[] CarrefourProxiOnly =
        [
            "carrefour proxi", "carrefour city", "carrefour contact",
            "carrefour express", "carrefour montagne",
            "crf proxi", "crfproxi",
        ];

        // Enseignes "Carrefour" qui bloquent vraiment tout Carrefour
        private static readonly string[] CarrefourFull =
        [
            "carrefour market", "carrefour hypermarche",
            "carrefour hypermarché", "carrefour drive",
            "carrefour.fr",
        ];

        /// <summary>Retourne true si le texte est une formulation d'EXCLUSION.</summary>
        private static bool IsExclusion(string t) =>
            t.Contains("pas valable")   || t.Contains("non valable")   ||
            t.Contains("n'est pas")     || t.Contains("à l'exclusion") ||
            t.Contains("a l'exclusion") || t.Contains("sauf chez")     ||
            t.Contains("sauf en")       || t.Contains("sauf carrefour")||
            t.Contains("hors leclerc")  || t.Contains("hors carrefour")||
            t.Contains("hors drive")    || t.Contains("ne sont pas")   ||
            t.Contains("hors auchan")   || t.Contains("hors casino")   ||
            t.Contains("hors monoprix") || t.Contains("hors franprix") ||
            t.Contains("ne sont valables que") ||
            // "toute enseigne vendante sauf X" = exclusion
            (t.Contains("toute enseigne") && t.Contains("sauf"));

        /// <summary>
        /// Retourne true si Carrefour est VRAIMENT exclu (Market/Hyper/Drive),
        /// false si seulement une sous-enseigne proxi/city est exclue.
        /// </summary>
        private static bool IsCarrefourFullyExcluded(string t)
        {
            // Vérifier d'abord si ce sont uniquement les sous-enseignes proxi
            bool onlyProxiExcluded = CarrefourProxiOnly.Any(s => t.Contains(s)) &&
                                     !CarrefourFull.Any(s => t.Contains(s)) &&
                                     !t.Contains("carrefour,") && // "Carrefour, Leclerc..."
                                     !Regex.IsMatch(t, @"carrefour(?!\s*(proxi|city|contact|express))");
            if (onlyProxiExcluded) return false;
            return t.Contains("carrefour");
        }

        /// <summary>
        /// EXPOSED : la même logique de détection ticket que v112, accessible
        /// depuis OfferAnalyzer pour les offres API.
        /// </summary>
        internal static (TicketType type, string? warningText) DetectTicketTypeFromText(string text)
            => DetectTicketType(text);

        private static (TicketType type, string? warningText) DetectTicketType(string html)
        {
            // Source 1 : warning popup
            var warnMatch = Regex.Match(html,
                @"warningPopup[\s\S]{0,100}webPopup-message[\s\S]{0,50}<p>([\s\S]{0,400}?)</p>",
                RegexOptions.IgnoreCase);
            var warnText = warnMatch.Success
                ? WebUtility.HtmlDecode(warnMatch.Groups[1].Value.Trim()) : null;

            // Source 2 : phrases de conditions si pas de popup
            if (string.IsNullOrEmpty(warnText))
            {
                // Chercher toutes les phrases avec enseigne
                var condMatch = Regex.Match(html,
                    @"(?:valable|pas valable|non valable|sauf|hors)[^.]{0,300}\.",
                    RegexOptions.IgnoreCase);
                if (condMatch.Success)
                    warnText = WebUtility.HtmlDecode(condMatch.Value.Trim());
            }

            if (string.IsNullOrEmpty(warnText))
                return (TicketType.Standard, null);

            var t = warnText.ToLowerInvariant();
            bool exclusion = IsExclusion(t);

            // ── Présence des enseignes ─────────────────────────────────────
            bool hasLeclerc     = t.Contains("leclerc");
            bool hasCarrefour   = t.Contains("carrefour");
            bool hasIntermarche = t.Contains("intermarché") || t.Contains("intermarche");
            bool hasSystemeU    = t.Contains("système u")   || t.Contains("systeme u") ||
                                  t.Contains("système-u");
            bool hasAuchan      = t.Contains("auchan");
            bool hasMonoprix    = t.Contains("monoprix");
            bool hasCasino      = t.Contains("casino");
            bool hasFranprix    = t.Contains("franprix");
            bool hasMagasin     = t.Contains("en magasin");

            // ── Drive exclusif ─────────────────────────────────────────────
            bool hasDriveExclusive =
                !hasMagasin &&
                (t.Contains("uniquement en drive") ||
                 t.Contains("valable en drive")    ||
                 t.Contains("uniquement drive")    ||
                 (t.Contains("drive") && t.Contains("livraison") &&
                  !hasLeclerc && !hasCarrefour && !hasIntermarche &&
                  !hasSystemeU && !hasAuchan && !hasMonoprix));

            bool hasCarrefourDriveNamed = t.Contains("carrefour drive") ||
                                          t.Contains("drive carrefour");

            // ── Mode INCLUSION (valable CHEZ X) ────────────────────────────
            if (!exclusion)
            {
                if (hasDriveExclusive || hasCarrefourDriveNamed)
                    return (TicketType.CarrefourDrive, warnText);

                // Priorité : Carrefour > Leclerc > Standard
                if (hasCarrefour)
                    return (TicketType.Standard, warnText);
                if (hasLeclerc)
                    return (TicketType.Leclerc, warnText);
                return (TicketType.Standard, warnText);
            }

            // ── Mode EXCLUSION (pas valable chez X / sauf X / hors X) ──────
            // Déterminer quelles enseignes sont réellement exclues
            bool carrefourExcluded = hasCarrefour && IsCarrefourFullyExcluded(t);
            bool lecclercExcluded  = hasLeclerc;
            bool auchanExcluded    = hasAuchan;
            bool casinoExcluded    = hasCasino;
            bool monoprixExcluded  = hasMonoprix;
            bool franprixExcluded  = hasFranprix;

            // "toute enseigne sauf X" → Standard (Carrefour disponible) sauf si Carrefour exclu
            bool touteEnseigne = t.Contains("toute enseigne") || t.Contains("toutes enseignes");

            if (touteEnseigne)
            {
                // Carrefour Market exclu → Leclerc
                if (carrefourExcluded && !lecclercExcluded)
                    return (TicketType.Leclerc, warnText);
                // Leclerc exclu mais Carrefour ok → Standard (Carrefour)
                if (!carrefourExcluded)
                    return (TicketType.Standard, warnText);
                // Carrefour ET Leclerc exclus → Standard (autre enseigne)
                return (TicketType.Standard, warnText);
            }

            // Cas classiques :
            // Carrefour exclu, Leclerc non exclu → Leclerc
            if (carrefourExcluded && !lecclercExcluded)
                return (TicketType.Leclerc, warnText);
            // Leclerc exclu, Carrefour non exclu → Standard (Carrefour)
            if (lecclercExcluded && !carrefourExcluded)
                return (TicketType.Standard, warnText);
            // Seuls Auchan/Casino/Monoprix/Franprix exclus → Standard (Carrefour ok)
            if (!carrefourExcluded && !lecclercExcluded &&
                (auchanExcluded || casinoExcluded || monoprixExcluded || franprixExcluded))
                return (TicketType.Standard, warnText);

            return (TicketType.Standard, warnText);
        }
        // ══════════════════════════════════════════════════════════════════════
        //  EXTRACTION RÉFÉRENCES ÉLIGIBLES — NOM + QUANTITÉ
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extrait le nom du produit depuis "Référence(s) éligible(s)"
        /// et sélectionne celui avec la plus grande quantité (ex: 16 portions > 12 portions).
        /// Met à jour data.ProductName et data.MaxPrice si trouvé avec prix.
        /// </summary>
        private static void ExtractEligibleReferences(string html, ProductData data)
        {
            // Bloc "Reference(s) eligible(s)" dans le HTML
            var blockMatch = Regex.Match(html,
                @"[Rr][eé]f[eé]rence(?:\(s\)|s)?\s+[eé]ligible(?:\(s\)|s)?\s*(?:et\s+prix[^:]*)?:([^<]*(?:<br[^>]*>[^<]*)*)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string rawBlock;
            if (blockMatch.Success)
            {
                rawBlock = WebUtility.HtmlDecode(
                    Regex.Replace(blockMatch.Groups[1].Value, "<[^>]+>", "\n"));
            }
            else
            {
                var condM = Regex.Match(data.RawConditions,
                    @"[Rr][eé]f[eé]rence(?:\(s\)|s)?\s+[eé]ligible(?:\(s\)|s)?[^:]*:(.+)",
                    RegexOptions.Singleline);
                if (!condM.Success) return;
                rawBlock = condM.Groups[1].Value;
            }

            var lines = rawBlock
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l =>
                {
                    var t = l.Trim();
                    while (t.Length > 0 && "-* ".IndexOf(t[0]) >= 0)
                        t = t.Length > 1 ? t.Substring(1).TrimStart() : "";
                    // Supprimer aussi em-dash U+2013 et bullet U+2022
                    while (t.Length > 0 && (t[0] == '\u2013' || t[0] == '\u2022'))
                        t = t.Length > 1 ? t.Substring(1).TrimStart() : "";
                    return t.Trim();
                })
                .Where(l => l.Length > 3)
                .ToList();

            if (lines.Count == 0) return;

            var refs = lines.Select(line =>
            {
                // Prix entre parentheses : "Nom (4,70EUR)"
                var priceM = Regex.Match(line, @"\(([\d,.]+)\s*€\)");
                double price = 0;
                string name  = line;
                if (priceM.Success)
                {
                    double.TryParse(priceM.Groups[1].Value.Replace(',', '.'),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out price);
                    name = line.Substring(0, priceM.Index).Trim();
                }

                // Quantite : detecter "16 portions", "x126", "3x60"
                int qty = 0;
                var qtyM = Regex.Match(name,
                    @"(\d+)\s*[xX]\s*(\d+)|(\d+)\s*(?:portions?|pcs?|g|kg|ml|cl|l)?",
                    RegexOptions.IgnoreCase);
                if (qtyM.Success)
                {
                    if (qtyM.Groups[1].Success && qtyM.Groups[2].Success)
                        qty = int.Parse(qtyM.Groups[1].Value) *
                              int.Parse(qtyM.Groups[2].Value);
                    else if (qtyM.Groups[3].Success)
                        int.TryParse(qtyM.Groups[3].Value, out qty);
                }

                return new { Name = name, Price = price, Qty = qty };
            }).ToList();

            if (refs.Count == 0) return;

            // Stocker toutes les références pour le panel de sélection manuelle
            data.AllEligibleRefs = refs
                .Select(r => new EligibleRef
                {
                    Name    = System.Net.WebUtility.HtmlDecode(r.Name ?? ""),
                    Price   = r.Price,
                    Barcode = "",   // ShopmiumParser n'a pas accès aux produits API ici
                                    // → sera enrichi par OfferAnalyzer.MakeRef() ensuite
                })
                .ToList();

            var best = refs
                .OrderByDescending(r => r.Price)
                .ThenByDescending(r => r.Qty)
                .First();

            if (!string.IsNullOrEmpty(best.Name))
                data.ProductName = System.Net.WebUtility.HtmlDecode(best.Name ?? "");
            if (best.Price > 0 && best.Price > data.MaxPrice)
            {
                data.MaxPrice = best.Price;
                data.MinPrice = refs.Where(r => r.Price > 0).Min(r => r.Price);
            }
        }

        // ── Si pas de références éligibles trouvées, fallback sur le prix puis h1 ──
        private static void ApplyProductNameFallback(ProductData data, string fallbackFromPrice, string fallbackH1)
        {
            if (!string.IsNullOrEmpty(data.ProductName)) return; // déjà rempli par les références
            // S'assurer que les noms de fallback sont décodés
            fallbackFromPrice = System.Net.WebUtility.HtmlDecode(fallbackFromPrice ?? "");
            fallbackH1        = System.Net.WebUtility.HtmlDecode(fallbackH1 ?? "");
            if (!string.IsNullOrEmpty(fallbackFromPrice))
                data.ProductName = fallbackFromPrice;
            else if (!string.IsNullOrEmpty(fallbackH1))
                data.ProductName = fallbackH1;
        }


        // ══════════════════════════════════════════════════════════════════════
        //  EXTRACTION CONDITIONS
        // ══════════════════════════════════════════════════════════════════════
        private static string ExtractConditionsText(HtmlDocument doc)
        {
            var blocks = doc.DocumentNode
                .SelectNodes("//div[contains(@class,'Offers-DetailsBlock')]");
            if (blocks != null)
            {
                var parts = new List<string>();
                foreach (var block in blocks)
                {
                    var paras = block.SelectNodes(".//p");
                    if (paras != null)
                        foreach (var p in paras)
                            parts.Add(WebUtility.HtmlDecode(p.InnerText.Trim()));
                }
                if (parts.Count > 0)
                    return string.Join("\n", parts.Where(s => !string.IsNullOrEmpty(s)));
            }
            var info = doc.DocumentNode
                .SelectSingleNode("//section[contains(@class,'details-informations')]");
            return WebUtility.HtmlDecode(info?.InnerText.Trim() ?? "");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DETECTION MULTI-CONDITIONS — LOGIQUE PRINCIPALE
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Détecte TOUTES les exigences de preuve avec leur texte exact.
        ///
        /// Mode A : Section numérotée "Preuves supplémentaires obligatoires"
        ///          → conserve le texte brut de chaque item pour le prompt IA
        /// Mode B : Analyse textuelle générale (fallback)
        /// </summary>
        /// <summary>
        /// Détecte les exigences UNIQUEMENT si la phrase exacte est présente :
        /// "Preuve d'achat supplémentaire obligatoire demandée pour toute demande de remboursement"
        /// Si cette phrase est absente → aucune exigence détectée.
        /// </summary>
        public static List<ProofRequirement> DetectAllRequirements(string conditions)
        {
            if (string.IsNullOrWhiteSpace(conditions)) return [];
            // Seule la section "Preuve supplémentaire obligatoire" compte
            return ExtractSupplementaryRequirements(conditions);
        }

        /// <summary>Compatibilité backward.</summary>
        public static List<ProofType> DetectAllProofTypes(string conditions) =>
            DetectAllRequirements(conditions).Select(r => r.Type).ToList();

        // ── Mode A : Parse les items numérotés — conserve le texte brut ─────
        private static List<ProofRequirement> ExtractSupplementaryRequirements(string conditions)
        {
            var result = new List<ProofRequirement>();
            var raw    = Normalize(conditions);

            // Détecter la phrase déclencheuse (variantes possibles)
            var triggerPatterns = new[]
            {
                @"preuves?\s+d.{1,5}achat\s+suppl[eé]mentaires?\s+obligatoires?",
                @"preuves?\s+suppl[eé]mentaires?\s+obligatoires?",
                @"preuve.{0,10}suppl[eé]mentaire.{0,10}oblig",
                @"justificatif.{0,20}suppl[eé]mentaire.{0,10}oblig",
            };

            int triggerPos = -1;
            foreach (var pat in triggerPatterns)
            {
                var m = Regex.Match(raw, pat, RegexOptions.IgnoreCase);
                if (m.Success) { triggerPos = m.Index + m.Length; break; }
            }

            if (triggerPos < 0) return result; // Phrase déclencheuse absente → aucune exigence

            // Extraire le texte après le déclencheur
            var afterTrigger = raw.Substring(triggerPos).Trim();

            // Cas 2 : texte inline après ":" sur la même ligne (pas de liste numérotée)
            // Ex: "... remboursement : Photo du code-barres rayé."
            if (!afterTrigger.Contains("\n") || !Regex.IsMatch(afterTrigger, @"\d+[\)\.]"))
            {
                // Texte court inline → traiter comme un seul item
                var inline = afterTrigger.TrimStart(':', ' ').Trim();
                if (!string.IsNullOrEmpty(inline) && inline.Length > 3)
                {
                    var t = ClassifyProofItem(inline);
                    if (t != ProofType.None)
                        result.Add(ProofRequirement.From(t, inline));
                }
                return result;
            }

            // Cas 1 : liste numérotée "1)" "2)" etc.
            var itemPattern = @"(?:^|\n)\s*(?:\d+[\)\.]\s*|[a-z][\)\.]\s*|[-•]\s*)(.+?)(?=\n\s*(?:\d+[\)\.]\s*|[a-z][\)\.]\s*|[-•]\s*)|\z)";
            var items = Regex.Matches(afterTrigger, itemPattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);

            if (items.Count == 0)
            {
                // Fallback : prendre les lignes non vides après le trigger
                var lines = afterTrigger
                .Split('\n')

                    .Where(l => l.Length > 8 && !l.StartsWith("//"))
                    .Take(5)
                    .ToList();
                foreach (var line in lines)
                {
                    var t = ClassifyProofItem(line);
                    if (t != ProofType.None && !result.Any(r => r.Type == t))
                        result.Add(ProofRequirement.From(t, line));
                }
            }
            else
            {
                foreach (Match m in items)
                {
                    var itemText = Normalize(m.Groups[1].Value.Trim());
                    if (itemText.Length < 5) continue;
                    var t = ClassifyProofItem(itemText);
                    if (t != ProofType.None && !result.Any(r => r.Type == t))
                        result.Add(ProofRequirement.From(t, itemText));
                }
            }

            return result;
        }

        /// <summary>
        /// Classifie un item individuel de preuve.
        /// Ex : "photo de la boite abimee" → PhotoEmballage
        ///      "code-barres raye" → PhotoBarcodeRaye
        /// </summary>
        public static ProofType ClassifyProofItem(string item)
        {
            var c = Normalize(item);

            // Code-barres rayé / barré
            if (Regex.IsMatch(c,
                @"code.{0,10}barre.{0,40}(ray|barr|biff|stylo|feutre|bic|coupe|trac)|" +
                @"(ray|barr|coupe).{0,20}(stylo|feutre|bic|marqueur|code)|" +
                @"barre.{0,20}(raye|coupe|barre)|" +
                @"code.{0,10}barre.{0,30}(raye|barre|coche|marque)"))
                return ProofType.PhotoBarcodeRaye;

            // Emballage abîmé / boîte déchirée / packaging ouvert
            if (Regex.IsMatch(c,
                @"(boite|boite|emballage|packaging|produit).{0,40}(abime|abime|dechire|dechire|ouvert|endommage|detruit|vide|consomme)|" +
                @"(abime|dechire|endommage|ouvert|vide|consomme).{0,40}(boite|emballage|packaging|produit)|" +
                @"photo.{0,30}(boite|emballage).{0,30}(abime|dechire|ouvert|entame)|" +
                @"(boite|emballage).{0,20}photo|" +
                @"photo.{0,30}emballage|" +
                @"photo.{0,20}(packaging|produit).{0,20}(complet|entier|face)|" +
                @"photo.{0,30}(boite|bouteille|pot|flacon)"))
                return ProofType.PhotoEmballage;

            // Étiquette prix
            if (Regex.IsMatch(c,
                @"etiquette.{0,20}prix|" +
                @"prix.{0,20}etiquette|" +
                @"photo.{0,30}prix.{0,20}(rayon|magasin)|" +
                @"label.{0,20}prix"))
                return ProofType.PhotoEtiquettePrix;

            // Photo de tous les articles
            if (Regex.IsMatch(c,
                @"photo.{0,50}(tous|all|ensemble|integralit).{0,30}article|" +
                @"(tous|all).{0,30}article.{0,30}photo|" +
                @"photo.{0,30}(les|vos|plusieurs).{0,20}(article|produit)|" +
                @"article.{0,30}achet.{0,30}photo|" +
                @"photo.{0,20}produit.{0,20}achet|" +
                @"photo.{0,20}article.{0,20}visible|" +
                @"prendre.{0,20}photo.{0,20}(article|produit)|" +
                @"photographie.{0,30}(article|produit)"))
                return ProofType.PhotoArticles;

            // Ticket de caisse
            if (Regex.IsMatch(c,
                @"photo.{0,30}ticket|" +
                @"ticket.{0,20}(caisse|achat)|" +
                @"photographi.{0,20}ticket"))
                return ProofType.Ticket;

            // Produit hors emballage
            if (Regex.IsMatch(c,
                @"(hors|en.dehors|sorti|retire|extrait).{0,30}(emballage|boite|packaging)|" +
                @"(emballage|boite|packaging).{0,30}(retire|ouvert|enlevé)|" +
                @"photo.{0,40}produit.{0,30}(hors|sans|dehors).{0,20}emballage|" +
                @"sans.{0,20}emballage|" +
                @"produit.{0,30}sortant.{0,20}emballage"))
                return ProofType.PhotoProduitHorsEmballage;

            // Emballage découpé au niveau du code-barres
            if (Regex.IsMatch(c,
                @"(decoupe|coupe|decouper|découpe|découper).{0,40}(code.barre|barcode|ean)|" +
                @"(code.barre|barcode).{0,40}(decoupe|coupe|retirer|decouper)|" +
                @"emballage.{0,30}(decoupe|coupe).{0,30}(code|barr)|" +
                @"photo.{0,40}(emballage|boite).{0,30}(decoupe|coupe)|" +
                @"(decouper|couper).{0,30}(emballage|boite).{0,30}(code|barr)|" +
                @"bandeau.{0,20}code|" +
                @"code.barres?.{0,20}decoupe"))
                return ProofType.PhotoEmballageDecoupe;

            // Produit / photo générique (dernier recours pour items de preuves)
            if (Regex.IsMatch(c,
                @"^photo\s+(de\s+la\s+|du\s+|d.{1,3})?(boite|produit|article|emballage)|" +
                @"(boite|produit|article).{0,30}photo"))
                return ProofType.PhotoEmballage;

            return ProofType.None;
        }

        // ── Matchers généraux (Mode B) ─────────────────────────────────────────
        private static bool MatchesBarcodeRaye(string c) =>
            Regex.IsMatch(c,
                @"code.{0,10}barre.{0,40}(ray|barr|biff|stylo|feutre|bic)|" +
                @"(ray|barr).{0,20}(stylo|feutre|bic|marqueur)|" +
                @"(stylo|feutre|marqueur).{0,30}code.{0,10}barre|" +
                @"code.{0,10}barre.{0,30}raye");

        private static bool MatchesPhotoArticles(string c) =>
            Regex.IsMatch(c,
                @"photo.{0,50}(tous|all|ensemble|integralit).{0,30}article|" +
                @"(tous|all|ensemble).{0,30}article.{0,30}photo|" +
                @"article.{0,30}achet.{0,30}photo|" +
                @"photo.{0,20}produit.{0,20}achet|" +
                @"photo.{0,20}article.{0,20}visible|" +
                @"photographie.{0,30}(article|produit)|" +
                @"prendre.{0,20}photo.{0,20}(article|produit)|" +
                @"photo.{0,10}obligatoire.{0,30}(article|produit)");

        private static bool MatchesEtiquettePrix(string c) =>
            Regex.IsMatch(c,
                @"etiquette.{0,20}prix|prix.{0,20}etiquette|" +
                @"photo.{0,30}(prix|tarif).{0,20}(rayon|magasin|linea)|" +
                @"label.{0,20}prix|photographi.{0,20}etiquette");

        private static bool MatchesEmballage(string c) =>
            Regex.IsMatch(c,
                @"photo.{0,30}emballage|photo.{0,30}packaging|" +
                @"emballage.{0,30}photo|" +
                @"photo.{0,30}(boite|bouteille|pot|flacon).{0,20}(compl|entier|face)");

        private static bool MatchesTicket(string c) =>
            Regex.IsMatch(c,
                @"photo.{0,30}ticket|" +
                @"ticket.{0,20}(caisse|achat).{0,20}photo|" +
                @"photographi.{0,20}ticket");

        // ── Normalisation ─────────────────────────────────────────────────────
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d  = s.ToLowerInvariant()
                      .Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (char ch in d)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        public static string DescribeProofType(ProofType type) => type switch
        {
            ProofType.None                  => "Ticket de caisse standard",
            ProofType.Ticket                => "Photo du ticket de caisse",
            ProofType.PhotoArticles         => "Photo de tous les articles",
            ProofType.PhotoBarcodeRaye      => "Code-barres raye au stylo/feutre",
            ProofType.PhotoEtiquettePrix    => "Photo etiquette prix en rayon",
            ProofType.PhotoEmballage        => "Photo de l emballage / boite",
            ProofType.PhotoArticlesEtTicket => "Photo articles + ticket de caisse",
            _ => "Inconnu"
        };
    }
}
