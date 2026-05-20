using ShopmiumPdfAutomator.Models;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ShopmiumPdfAutomator.Services
{
    public static class PhotoshopService
    {
        public record RenderResult(string PngPath, byte[] PreviewPngBytes);

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(
            ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        private static object? TryGetActivePhotoshop()
        {
            try
            {
                var clsid = Type.GetTypeFromProgID("Photoshop.Application")?.GUID ?? Guid.Empty;
                if (clsid == Guid.Empty) return null;
                GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
                return obj;
            }
            catch { return null; }
        }

        public static RenderResult Render(ProductData data, string outputDir,
            IProgress<string>? progress = null)
        {
            var outputPngPath = Path.Combine(outputDir,
                $"ticket_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var pngPath = Path.Combine(Path.GetTempPath(),
                $"shopmium_prev_{Guid.NewGuid():N}.png");
            var logPath = Path.Combine(Path.GetTempPath(),
                $"shopmium_log_{Guid.NewGuid():N}.txt");

            var psdPath = GetTemplatePath(data.TicketType);
            var jsxCode = BuildJsx(data, psdPath, outputPngPath, pngPath, logPath);

            Exception? err = null;
            var sta = new Thread(() =>
            {
                try { RunCom(jsxCode, logPath, progress); }
                catch (Exception ex) { err = ex; }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.IsBackground = true;
            sta.Start();
            sta.Join(TimeSpan.FromMinutes(5));

            if (err != null) throw err;

            if (!File.Exists(outputPngPath))
            {
                var log = File.Exists(logPath)
                    ? File.ReadAllText(logPath, Encoding.UTF8) : "(aucun log)";
                throw new Exception($"Photoshop n'a pas cree le PNG.\n\nLog :\n{log}");
            }

            var prev = File.Exists(pngPath) ? File.ReadAllBytes(pngPath) : Array.Empty<byte>();
            try { File.Delete(logPath); } catch { }
            progress?.Report("PNG cree avec succes !");
            return new RenderResult(outputPngPath, prev);
        }

        public static Task<RenderResult> RenderAsync(ProductData data, string outputDir,
            IProgress<string>? progress = null) =>
            Task.Run(() => Render(data, outputDir, progress));

        private static void RunCom(string jsxCode, string logPath,
            IProgress<string>? progress)
        {
            dynamic? ps = null;
            try
            {
                progress?.Report("Connexion a Photoshop...");

                // Tentative 1 : récupérer une instance déjà ouverte
                ps = TryGetActivePhotoshop();
                if (ps != null)
                {
                    progress?.Report("Photoshop deja ouvert — utilisation de l'instance existante.");
                }
                else
                {
                    // Tentative 2 : créer une nouvelle instance via ProgID
                    var t = Type.GetTypeFromProgID("Photoshop.Application");
                    if (t == null)
                    {
                        // Photoshop non enregistré dans le registre COM
                        // → essayer via le chemin direct de l'exe
                        throw new InvalidOperationException(
                            "Photoshop n'est pas détecté sur ce PC.\n\n" +
                            "Assurez-vous que :\n" +
                            "1. Adobe Photoshop est installé\n" +
                            "2. Photoshop a été lancé au moins une fois\n" +
                            "3. Utilisez le .exe directement (pas le setup)\n\n" +
                            "Si Photoshop est installé mais l'erreur persiste,\n" +
                            "lancez Photoshop manuellement puis réessayez.");
                    }

                    progress?.Report("Demarrage Photoshop...");
                    try
                    {
                        ps = Activator.CreateInstance(t);
                    }
                    catch (COMException comEx) when ((uint)comEx.HResult == 0x80080005)
                    {
                        // CO_E_SERVER_EXEC_FAILURE : Photoshop refuse de démarrer via COM
                        // → souvent causé par une installation via setup qui change les droits
                        throw new InvalidOperationException(
                            "Photoshop ne répond pas à l'appel COM (80080005).\n\n" +
                            "Solutions :\n" +
                            "1. Lancez Photoshop MANUELLEMENT, attendez qu'il soit ouvert,\n" +
                            "   puis cliquez à nouveau sur Générer\n" +
                            "2. Utilisez le .exe directement (pas le raccourci du setup)\n" +
                            "3. Désinstallez et réinstallez Photoshop si nécessaire", comEx);
                    }

                    if (ps == null)
                        throw new InvalidOperationException("Impossible de créer l'instance Photoshop.");

                    // Attendre que Photoshop soit prêt (plus long si premier démarrage)
                    progress?.Report("Photoshop demarre — attente...");
                    Thread.Sleep(6000);
                }

                try { ps.Visible = true;         } catch { }
                try { ps.DisplayDialogs = 3;      } catch { }

                progress?.Report("Execution du script JSX...");
                ps.DoJavaScript(jsxCode);
                progress?.Report("Photoshop termine.");
            }
            finally
            {
                if (ps != null)
                {
                    try { ps.DisplayDialogs = 0; } catch { }
                    try { Marshal.ReleaseComObject(ps); } catch { }
                }
            }
        }

        private static string BuildJsx(ProductData data, string psdPath,
            string outputPngPath, string pngPath, string logPath)
        {
            return data.TicketType switch
            {
                ShopmiumPdfAutomator.Models.TicketType.Leclerc
                    => BuildJsxLeclerc(data, psdPath, outputPngPath, pngPath, logPath),
                ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive
                    => BuildJsxCarrefourDrive(data, psdPath, outputPngPath, pngPath, logPath),
                _   => BuildJsxStandard(data, psdPath, outputPngPath, pngPath, logPath),
            };
        }

        // ── Ticket standard (template.psd) ───────────────────────────────────
        private static string BuildJsxStandard(ProductData data, string psdPath,
            string outputPngPath, string pngPath, string logPath)
        {
            var (timeColon, timeH) = EnsureTime(data);
            var (prodL1, prodL2)   = FormatProduct(data.ProductName);

            var date39 = $"{data.StartDate} {timeH}";
            var date09 = FormatDate09(data.StartDate, timeColon);
            var prod   = $"{prodL1.Trim()}\r{prodL2.Trim()}";
            var qty    = $"{data.MaxArticles} x {data.MaxPrice:F2}";
            var total  = $"{data.TotalTTC:F2}";
            var totalE = $"{data.TotalTTC:F2}\u20AC";
            var tvaE   = $"{data.TvaAmount:F2}\u20AC";
            var tvaPct = $"{data.TvaRate * 100:0.0}%";

            static string J(string s) =>
                s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                 .Replace("\r", "\\r").Replace("\n", "\\n");
            static string P(string p) => p.Replace('\\', '/');

            var pairs = new (string Name, string Value)[]
            {
                ("date_ticket",                          date39),
                ("ref_ticket",                           date09),
                ("product_name",                         prod),
                ("qty_price",                            qty),
                ("amount_line",                          total),
                ("tva_rate_col",                         tvaPct),
                ("tva_rate_sec",                         tvaPct),
                ("total_payer",                          totalE),
                ("total_bancaire",                       totalE),
                ("total_produits",                       totalE),
                ("tva_total_prod",                       totalE),
                ("tva_amount_col",                       tvaE),
                ("tva_amount_sec",                       tvaE),
                ("13/02/2026 \u00e0 10h18",              date39),
                ("13.02.26 10:18 9318 1 7938 0452",      date09),
                ("TOUCHER MAGIQUE 4D ULTRA CONFOR",       prod),
                ("5 x 3.99",                              qty),
                ("19.95",                                 total),
                ("10.0%",                                 tvaPct),
                ("19.95\u20AC",                           totalE),
                ("1.99\u20AC",                            tvaE),
            };

            // Pairs spécifiques au ticket Carrefour Drive
            // → Modifier uniquement l'article "Sacs réutilisables consignés Drive"
            List<(string Name, string Value)> extraPairs = [];
            if (data.TicketType == ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive)
            {
                var rnd2 = new Random();
                // Numéros aléatoires réalistes
                var orderNum  = rnd2.Next(700000000, 750000000).ToString();
                var invoiceId = $"FOM500{rnd2.Next(2400000, 2500000)}{rnd2.Next(100000, 999999)}";
                var clientId  = rnd2.Next(2000000, 3000000).ToString();

                extraPairs.AddRange([
                    // Ligne produit principal : remplacer "Sacs réutilisables consignés Drive"
                    ("9713236189234",                     data.BarcodeEan ?? "9713236189234"),
                    ("Sacs réutilisables consignés Drive", data.ProductName),
                    // Prix et quantité du produit
                    ("8",                                 data.MaxArticles.ToString()),
                    ("4",                                 data.MaxArticles.ToString()),
                    ("0.29",                              $"{data.MaxPrice / data.MaxArticles:F2}"),
                    ("0.35",                              $"{data.MaxPrice / data.MaxArticles:F2}"),
                    ("1.40",                              $"{data.TotalTTC:F2}"),
                    // TVA
                    ("20.0",                              $"{data.TvaRate * 100:0.0}"),
                    // Numéros de commande/facture
                    ("711421844",                         orderNum),
                    ("2231056",                           clientId),
                    ("FOM5002400503161",                  invoiceId),
                    // Dates
                    ("25/11/2024",                        data.StartDate),
                ]);
            }

            var allPairs = pairs.Concat(extraPairs);
            var jsArray = string.Join(",\n    ",
                allPairs.Select(p => $"[\"{J(p.Name)}\", \"{J(p.Value)}\"]"));

            // Valeur produit pour matching partiel — échappée séparément
            var prodJsx = J(prod);

            return $@"// Shopmium PNG Automator v18 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}
(function() {{

var LOG = ""{P(logPath)}"";
function log(m) {{
    var f = new File(LOG);
    f.encoding = ""UTF-8""; f.open(""a"");
    f.writeln(new Date().toLocaleTimeString() + "" | "" + m);
    f.close();
}}

var MODS = [
    {jsArray}
];

var changes = {{}};
for (var i = 0; i < MODS.length; i++) {{
    changes[MODS[i][0]] = MODS[i][1];
}}

// Valeur du produit pour le matching par contenu
var PROD_VAL = ""{prodJsx}"";

function modifyAllTextLayers(layers) {{
    var count = 0;
    for (var i = 0; i < layers.length; i++) {{
        var lyr = layers[i];
        if (lyr.typename === ""LayerSet"") {{
            count += modifyAllTextLayers(lyr.layers);
        }} else if (lyr.typename === ""ArtLayer"" && lyr.kind === LayerKind.TEXT) {{
            var name = lyr.name;
            var newVal = null;

            // 1. Matching exact par nom
            if (changes.hasOwnProperty(name)) {{
                newVal = changes[name];
            }}

            // 2. Matching par contenu pour le calque produit
            // Si le texte actuel contient TOUCHER ou CONFORT = calque produit
            if (newVal === null) {{
                var curTxt = """";
                try {{ curTxt = lyr.textItem.contents; }} catch(ex) {{}}
                if (curTxt.indexOf(""TOUCHER"") >= 0 || curTxt.indexOf(""CONFORT"") >= 0
                    || name.indexOf(""TOUCHER"") >= 0 || name.indexOf(""MAGIQUE"") >= 0) {{
                    newVal = PROD_VAL;
                }}
            }}

            if (newVal !== null) {{
                try {{
                    lyr.textItem.contents = newVal;
                    log(""  OK: '"" + name + ""'"");
                    count++;
                }} catch(e2) {{
                    log(""  ERR '"" + name + ""': "" + e2.message);
                }}
            }}
        }}
    }}
    return count;
}}

try {{
    log(""=== Debut v14b ==="");
    app.displayDialogs = DialogModes.NO;

    var pf = new File(""{P(psdPath)}"");
    if (!pf.exists) throw new Error(""PSD introuvable : {P(psdPath)}"");
    var doc = app.open(pf);
    log(""PSD ouvert : "" + doc.name);

    var nbMods = modifyAllTextLayers(doc.layers);
    log(""Calques modifies : "" + nbMods);

    var dCopy = doc.duplicate();
    dCopy.flatten();

    // Export PNG haute qualite (300 DPI, compression minimale)
    var pngSaveOpts = new PNGSaveOptions();
    pngSaveOpts.compression = 1;
    pngSaveOpts.interlaced  = false;

    dCopy.saveAs(new File(""{P(outputPngPath)}""), pngSaveOpts, true, Extension.LOWERCASE);
    log(""PNG_FINAL OK"");

    dCopy.resizeImage(
        UnitValue(800, ""px""),
        UnitValue(Math.round(dCopy.height * (800 / dCopy.width)), ""px""),
        72, ResampleMethod.BICUBIC
    );
    var expOpts = new ExportOptionsSaveForWeb();
    expOpts.format    = SaveDocumentType.PNG;
    expOpts.PNG8      = false;
    expOpts.quality   = 100;
    expOpts.interlaced = false;
    dCopy.exportDocument(new File(""{P(pngPath)}""), ExportType.SAVEFORWEB, expOpts);
    log(""PNG OK"");

    dCopy.close(SaveOptions.DONOTSAVECHANGES);
    doc.close(SaveOptions.DONOTSAVECHANGES);
    log(""=== Termine avec succes ==="");

}} catch(e) {{
    log(""ERREUR FATALE : "" + e.message + "" (ligne "" + e.line + "")"");
}}

try {{ app.displayDialogs = DialogModes.ALL; }} catch(x) {{}}
}})();
";
        }

        // ── Helper : formater une ligne avec montant aligné à droite ──────────
        // Utilisé pour le ticket Leclerc (espaces insécables \u00A0 = \xa0)
        private static string LeclercLine(string label, decimal amount, int width)
        {
            var amtStr   = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var padCount = width - label.Length - amtStr.Length;
            if (padCount < 1) padCount = 1;
            return label + new string('\u00A0', padCount) + amtStr + "\r";
        }

        // ── Helper : parser une date "dd/MM/yyyy" ─────────────────────────────
        private static System.DateTime ParseStartDate(string? raw)
        {
            if (!string.IsNullOrEmpty(raw) &&
                System.DateTime.TryParseExact(raw, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return System.DateTime.Now;
        }

        // ════════════════════════════════════════════════════════════════════
        //  JSX — Ticket LECLERC
        // ════════════════════════════════════════════════════════════════════
        private static string BuildJsxLeclerc(ProductData data, string psdPath,
            string outputPngPath, string pngPath, string logPath)
        {
            var (timeColon, _) = EnsureTime(data);
            var rnd = new System.Random();
            var dt  = ParseStartDate(data.StartDate);
            var fr  = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

            var ddMMyy      = dt.ToString("dd/MM/yy");
            var ddMoisYYYY  = dt.ToString("dd MMMM yyyy", fr);

            var price = data.MaxPrice;
            var qty   = data.MaxArticles;
            var total = data.TotalTTC;

            var prodName  = data.ProductName;
            var qtyLabel  = qty.ToString() + "\u00A0X\u00A0"
                            + price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                            + "\u20AC";
            var qtyLine   = LeclercLine(qtyLabel, (decimal)price, 31);
            var totalLine = LeclercLine(
                "Total\u00A0" + qty + "\u00A0article" + (qty > 1 ? "s\u00A0" : "\u00A0"), (decimal)total, 40);
            var cbLine    = LeclercLine("CB\u00A0", (decimal)total, 42);

            var caissN    = rnd.Next(1, 30).ToString("D3");
            var caissSub  = rnd.Next(1000, 9999).ToString("D4");
            var seq5      = rnd.Next(10000, 99999).ToString("D5");
            var autoN     = rnd.Next(100000, 999999).ToString();

            var ticketRef  = "Ticket\u00A0" + ddMMyy + "\u00A00\u00A0" + caissN + "SH\u00A0" + seq5;
            var caisseDate = "Caisse\u00A0" + caissN + "-" + caissSub + "\u00A0" + ddMoisYYYY
                           + "\u00A0" + timeColon[..5];
            var barcode    = "9900" + rnd.Next(10000000, 99999999)
                           + rnd.Next(1000, 9999) + caissN + "SH" + seq5;
            var cbDate     = "le\u00A0" + ddMMyy + "\u00A0a\u00A0" + timeColon;
            var noAuto     = "No\u00A0AUTO\u00A0:\u00A0" + autoN;
            // MONTANT = XX,XX EUR  (nouveau calque v2)
            var montantFr  = total.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            var montantLine = "MONTANT = " + montantFr + " EUR";

            static string J(string s) =>
                s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                 .Replace("\r", "\\r").Replace("\n", "\\n");
            static string P(string p) => p.Replace('\\', '/');

            return $@"// Shopmium Leclerc — {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}
(function() {{

var LOG = ""{P(logPath)}"";
function log(m) {{
    var f = new File(LOG); f.encoding=""UTF-8""; f.open(""a"");
    f.writeln(new Date().toLocaleTimeString()+"" | ""+m); f.close();
}}

var V = {{
    prod  : ""{J(prodName)}\r"",
    qty   : ""{J(qtyLine)}"",
    total : ""{J(totalLine)}"",
    cb    : ""{J(cbLine)}"",
    tref  : ""{J(ticketRef)}\r"",
    caisse: ""{J(caisseDate)}\r"",
    bcode : ""{J(barcode)}\r"",
    cbdate: ""{J(cbDate)}"",
    noauto : ""{J(noAuto)}"",
    montant: ""{J(montantLine)}\r""
}};

function classify(name) {{
    var n = name;
    if (n.indexOf(""PREP FROM FONDUE"") >= 0) return ""prod"";
    if (n.charAt(0) >= ""0"" && n.charAt(0) <= ""9"" &&
        n.indexOf("" X "") >= 0 && n.indexOf(""€"") >= 0) return ""qty"";
    if (n.indexOf(""Total"") >= 0 && n.indexOf(""article"") >= 0) return ""total"";
    if (n.length > 10 && n.indexOf(""CB"") === 0) return ""cb"";
    if (n.indexOf(""Ticket "") === 0) return ""tref"";
    if (n.indexOf(""Caisse "") === 0) return ""caisse"";
    if (n.indexOf(""9900"") === 0 && n.length > 15) return ""bcode"";
    if (n.indexOf(""le "") === 0 && n.indexOf("" a "") >= 0) return ""cbdate"";
    if (n.indexOf(""No AUTO"") >= 0) return ""noauto"";
    if (n.indexOf(""MONTANT"") >= 0) return ""montant"";
    return null;
}}

function modifyAll(layers) {{
    var count = 0;
    for (var i = 0; i < layers.length; i++) {{
        var lyr = layers[i];
        if (lyr.typename === ""LayerSet"") {{ count += modifyAll(lyr.layers); continue; }}
        if (lyr.typename !== ""ArtLayer"" || lyr.kind !== LayerKind.TEXT) continue;
        var tag = classify(lyr.name);
        if (!tag || !V[tag]) continue;
        try {{
            lyr.textItem.contents = V[tag];
            log(""OK ["" + tag + ""]: "" + lyr.name.substring(0, 25));
            count++;
        }} catch(e) {{ log(""ERR ["" + tag + ""]: "" + e.message); }}
    }}
    return count;
}}

try {{
    log(""=== LECLERC START ==="");
    app.displayDialogs = DialogModes.NO;
    var pf = new File(""{P(psdPath)}"");
    if (!pf.exists) throw new Error(""PSD introuvable"");
    var doc = app.open(pf);
    log(""PSD: "" + doc.name + "" — calques modifies: "" + modifyAll(doc.layers));
    var opts = new PNGSaveOptions(); opts.compression = 6;
    doc.saveAs(new File(""{P(pngPath)}""),        opts, true, Extension.LOWERCASE);
    doc.saveAs(new File(""{P(outputPngPath)}""), opts, true, Extension.LOWERCASE);
    doc.close(SaveOptions.DONOTSAVECHANGES);
    log(""=== DONE ==="");
}} catch(e) {{ log(""ERREUR: "" + e.message); }}

}})();
";
        }

        // ════════════════════════════════════════════════════════════════════
        //  JSX — Ticket CARREFOUR DRIVE  (PSD v85)
        //  Remise=0 | Carte Bancaire | TVA dynamique | reset autres colonnes
        // ════════════════════════════════════════════════════════════════════
        private static string BuildJsxCarrefourDrive(ProductData data, string psdPath,
            string outputPngPath, string pngPath, string logPath)
        {
            var rnd   = new System.Random();
            var dt    = ParseStartDate(data.StartDate);
            var ic    = System.Globalization.CultureInfo.InvariantCulture;
            var dateF = dt.ToString("dd/MM/yyyy", ic);

            var priceTTC = data.MaxPrice;
            var total    = data.TotalTTC;
            var tvaRate  = data.TvaRate * 100.0;
            var ean      = string.IsNullOrWhiteSpace(data.BarcodeEan)
                           ? "8006540792728" : data.BarcodeEan;
            var hasEanJs = string.IsNullOrWhiteSpace(data.BarcodeEan) ? "false" : "true";

            var tvaStr   = (tvaRate % 1.0 == 0)
                           ? ((int)tvaRate).ToString(ic)
                           : tvaRate.ToString("G", ic);

            var ht        = Math.Round(total / (1.0 + data.TvaRate), 2);
            var tvaAmount = Math.Round(total - ht, 2);

            var orderNum  = rnd.Next(500000000, 699999999).ToString();
            var clientId  = rnd.Next(1000000, 9999999).ToString();
            var webPart1  = rnd.Next(1000, 9999).ToString("D6");
            var webPart2  = rnd.Next(10000000, 99999999).ToString();
            var invoiceId = $"WEB-{webPart1}-{webPart2}";

            var f2PxTTC  = priceTTC.ToString("F2", ic);
            var f2Total  = total.ToString("F2", ic);
            var f2HT     = ht.ToString("F2", ic);
            var f2TvaAmt = tvaAmount.ToString("F2", ic);
            var nbArt    = (data.MaxArticles + 1).ToString();

            // ── Identité française aléatoire (mais réaliste & cohérente) ──────
            // Ces variables remplacent les calques d'origine du PSD Carrefour Drive :
            //   "MME"                       → identity.Title (MR ou MME)
            //   "BEN YOUSSEF Nesrine"       → identity.FullName (NOM Prénom)
            //   "12"                        → identity.StreetNum (numéro de rue)
            //   "rue"                       → identity.StreetType (rue/avenue/place...)
            //   "gaston monmousseau"        → identity.StreetName (nom de la voie)
            //   "FRANCE"                    → identity.Country (toujours FRANCE)
            //   "94200 IVRY SUR SEINE"      → identity.ZipAndCity (CP + ville cohérents)
            var identity = FrenchIdentityGenerator.Generate();

            static string P(string p) => p.Replace('\\', '/');

            // Escaper pour JavaScript (guillemet et backslash)
            static string Esc(string s) =>
                (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

            return $@"// Shopmium CarrefourDrive v85
(function() {{

var LOG = ""{P(logPath)}"";
function log(m) {{
    var f = new File(LOG); f.encoding=""UTF-8""; f.open(""a"");
    f.writeln(new Date().toLocaleTimeString()+"" | ""+m); f.close();
}}

var HAS_EAN     = {hasEanJs};
var V_EAN       = ""{ean}\r"";
var V_PROD      = ""{data.ProductName}\r"";
var V_QTY       = ""{data.MaxArticles}\r"";
var V_TVA       = ""{tvaStr}\r"";
var V_PX_TTC    = ""{f2PxTTC}\r"";
var V_MONTANT   = ""{f2Total}\r"";
var V_DATE      = ""{dateF}\r"";
var V_ORDER     = ""N\u00B0 de commande {orderNum}\r"";
var V_CLIENT    = ""Identifiant client {clientId}\r"";
var V_INVOICE   = ""N\u00B0 de facture {invoiceId}\r"";
var V_TOTAL     = ""{f2Total}\r"";
var V_HT        = ""{f2HT}\r"";
var V_TVA_AMT   = ""{f2TvaAmt}\r"";
var V_ZERO      = ""0.00\r"";
var V_NB_ART    = ""Nombres d\u2019articles remis : {nbArt}\r"";
var V_MERCI     = ""Merci pour votre commande Carrefour Drive du {dateF}\r"";
var V_TVA_LABEL = ""TVA "" + V_TVA.replace(""\r"", """");

// ── IDENTITÉ + ADRESSE (générées aléatoirement, françaises, cohérentes) ──
var V_TITLE       = ""{Esc(identity.Title)}"";
var V_FULLNAME    = ""{Esc(identity.FullName)}"";
var V_STREET_NUM  = ""{Esc(identity.StreetNum)}"";
var V_STREET_TYPE = ""{Esc(identity.StreetType)}"";
var V_STREET_NAME = ""{Esc(identity.StreetName)}"";
var V_COUNTRY     = ""{Esc(identity.Country)}"";
var V_ZIP_CITY    = ""{Esc(identity.ZipAndCity)}"";

function set(lyr, val) {{
    try {{
        lyr.textItem.contents = val;
        try {{
            if (lyr.textItem.kind === TextType.PARAGRAPHTEXT) {{
                var bb = lyr.textItem.boundingBox;
                lyr.textItem.boundingBox = [bb[0], bb[1], bb[2] + 80, bb[3]];
            }}
        }} catch(be) {{}}
        log(""OK '"" + lyr.name.substring(0,18) + ""'"");
    }} catch(e) {{ log(""ERR '"" + lyr.name + ""': "" + e.message); }}
}}

function collectText(layers, arr) {{
    for (var i = 0; i < layers.length; i++) {{
        if (layers[i].typename === ""LayerSet"") collectText(layers[i].layers, arr);
        else if (layers[i].typename === ""ArtLayer"" && layers[i].kind === LayerKind.TEXT)
            arr.push(layers[i]);
    }}
}}

function findFirst(all, name) {{
    for (var k = 0; k < all.length; k++) if (all[k].name === name) return k;
    return -1;
}}

function findAfter(all, from, name) {{
    for (var k = from + 1; k < all.length; k++) if (all[k].name === name) return k;
    return -1;
}}

function findNear(all, si, name, range) {{
    var from = Math.max(0, si - range);
    var to   = Math.min(all.length - 1, si + range);
    for (var k = from; k <= to; k++) if (all[k].name === name) return k;
    return -1;
}}

// Remplace le contenu du calque et restaure le bord DROIT original
// => alignement identique au 0.00 d'origine, pixel-perfect
function setTVANum(lyr, val) {{
    try {{
        var r0 = +lyr.bounds[2];
        lyr.textItem.contents = val;
        var r1 = +lyr.bounds[2];
        var dx = r0 - r1;
        if (dx > 0.1 || dx < -0.1) {{
            lyr.translate(new UnitValue(dx, 'px'), new UnitValue(0, 'px'));
        }}
    }} catch(te) {{ log(""setTVANum: "" + te.message); }}
}}

function modifyDrive(doc) {{
    var all = [];
    collectText(doc.layers, all);
    log(""Calques: "" + all.length);

    var si = -1;
    for (var i = 0; i < all.length; i++) {{
        if (all[i].name.indexOf(""Couches"") >= 0 || all[i].name.indexOf(""Baby-Dry"") >= 0
            || all[i].name.indexOf(""PAMPERS"") >= 0) {{ si = i; break; }}
    }}
    if (si < 0) {{ log(""ERREUR: Pampers introuvable""); return; }}

    if (HAS_EAN) {{
        var iEAN = findNear(all, si, ""8006540792728"", 5);
        if (iEAN >= 0) set(all[iEAN], V_EAN);
    }}
    set(all[si], V_PROD);

    var iQtyCmd = findNear(all, si, ""2"", 8);
    var iQtyLiv = (iQtyCmd >= 0) ? findAfter(all, iQtyCmd, ""2"") : -1;
    if (iQtyLiv > si + 8) iQtyLiv = -1;
    var iTva   = findNear(all, si, ""20"",    8);
    var iPxTTC = findNear(all, si, ""36.85"", 8);
    var iMon   = findNear(all, si, ""73.70"", 8);
    if (iQtyCmd >= 0) set(all[iQtyCmd], V_QTY);
    if (iQtyLiv >= 0) set(all[iQtyLiv], V_QTY);
    if (iTva    >= 0) set(all[iTva],    V_TVA);
    if (iPxTTC  >= 0) set(all[iPxTTC],  V_PX_TTC);
    if (iMon    >= 0) set(all[iMon],    V_MONTANT);

    for (var j = 0; j < all.length; j++) {{
        if (all[j].name === ""10/11/2023"") set(all[j], V_DATE);
        if (all[j].name.indexOf(""Merci pour votre commande Carrefour Drive du"") >= 0)
            set(all[j], V_MERCI);
    }}

    for (var j = 0; j < all.length; j++) {{
        var n = all[j].name;
        if (n.indexOf(""N\u00B0 de commande"") >= 0 && n.indexOf(""facture"") < 0)
            set(all[j], V_ORDER);
        else if (n.indexOf(""Identifiant client"") >= 0)
            set(all[j], V_CLIENT);
        else if (n.indexOf(""N\u00B0 de facture"") >= 0)
            set(all[j], V_INVOICE);
    }}

    // [057] Total panier TTC = V_TOTAL
    var iTPT = findFirst(all, ""Total panier TTC (apr\u00E8s remises)"");
    if (iTPT >= 0) {{
        var iPanAmt = findAfter(all, iTPT, ""73.70"");
        if (iPanAmt >= 0) set(all[iPanAmt], V_TOTAL);
    }}

    // [063] Total TTC en Euros #1 + [067] Carte Bancaire + [069] Total TTC #2
    var iTtc1 = findFirst(all, ""Total TTC en Euros"");
    if (iTtc1 >= 0) {{
        var iAmt1 = findAfter(all, iTtc1, ""73.70"");
        if (iAmt1 >= 0) set(all[iAmt1], V_TOTAL);
    }}
    var iCB = findFirst(all, ""Carte Bancaire"");
    if (iCB >= 0) {{
        var iCBAmt = findAfter(all, iCB, ""73.70"");
        if (iCBAmt >= 0) set(all[iCBAmt], V_TOTAL);
    }}
    var iTtc2 = (iTtc1 >= 0) ? findAfter(all, iTtc1 + 1, ""Total TTC en Euros"") : -1;
    if (iTtc2 >= 0) {{
        var iAmt2 = findAfter(all, iTtc2, ""73.70"");
        if (iAmt2 >= 0) set(all[iAmt2], V_TOTAL);
    }}

    // [064] Nombres d'articles remis = MaxArticles + 1
    for (var j = 0; j < all.length; j++) {{
        if (all[j].name.indexOf(""Nombres d"") >= 0 && all[j].name.indexOf(""articles remis"") >= 0)
            set(all[j], V_NB_ART);
    }}

    // ── Tableau TVA ───────────────────────────────────────────────────────
    // Photoshop traverse les calques en ordre INVERSE de Python/psd-tools.
    // Python : [TVA_label][HT][TVA_amt][TVA_label2]...
    // JSX    : ...[TVA_label2][TVA_amt][HT][TVA_label]
    // Donc pour chaque label TVA au JSX index iCol :
    //   iCol - 1 = calque HT de cette colonne
    //   iCol - 2 = calque TVA amount de cette colonne
    //
    // Colonne Total ([073][074]) :
    //   iTva0 + 1 = [074] TVA total
    //   iTva0 + 2 = [073] HT total

    // [073][074] Totaux HT/TVA colonne Total
    var iTva0 = findFirst(all, ""TVA 0"");
    if (iTva0 >= 0) {{
        var iHT_tot  = iTva0 + 2;
        var iTVA_tot = iTva0 + 1;
        if (iHT_tot  < all.length) setTVANum(all[iHT_tot],  V_HT);
        if (iTVA_tot < all.length) setTVANum(all[iTVA_tot], V_TVA_AMT);
    }}

    // Tableau TVA : colonne correcte = valeurs calculees, autres = 0.00
    // iCol-1 = HT de la colonne, iCol-2 = TVA amount de la colonne
    // setTVANum restaure le bord droit original => alignement pixel-perfect
    var tvaColumns = [""TVA 0"", ""TVA 5.5"", ""TVA 10"", ""TVA 20""];
    for (var c = 0; c < tvaColumns.length; c++) {{
        var iCol = findFirst(all, tvaColumns[c]);
        if (iCol < 0) continue;
        var iColHT  = iCol - 1;
        var iColTVA = iCol - 2;
        if (iColHT  < 0 || iColTVA < 0) continue;
        if (tvaColumns[c] === V_TVA_LABEL) {{
            setTVANum(all[iColHT],  V_HT);
            setTVANum(all[iColTVA], V_TVA_AMT);
        }} else {{
            setTVANum(all[iColHT],  V_ZERO);
            setTVANum(all[iColTVA], V_ZERO);
        }}
    }}

    // ── Cohérence : tous les montants 73.70 → V_TOTAL ─────────────────────
    // (remise=0 donc tous les totaux sont identiques)
    // On cherche UNIQUEMENT dans la zone inferieure (apres Total de vos remises)
    var iZoneStart = findFirst(all, ""Total de vos remises"");
    if (iZoneStart >= 0) {{
        for (var j = 0; j < all.length; j++) {{
            // Rechercher par nom de calque 73.70
            if (all[j].name === ""73.70"") set(all[j], V_TOTAL);
        }}
    }}

    // ── Identité + adresse (calques fixes du template Carrefour Drive) ────
    // Ces calques sont identifiés par leur NOM de calque dans le PSD, pas
    // par leur contenu (car le contenu d'origine est unique).
    for (var j = 0; j < all.length; j++) {{
        var n = all[j].name;
        // Civilité : exact ""MME"" (ou variations) — distinct du nom complet
        if (n === ""MME"" || n === ""MR"" || n === ""M."" || n === ""Mme"" || n === ""M"") {{
            set(all[j], V_TITLE);
        }}
        // Nom complet (calque ""BEN YOUSSEF Nesrine"")
        else if (n === ""BEN YOUSSEF Nesrine"" || n.indexOf(""BEN YOUSSEF"") >= 0) {{
            set(all[j], V_FULLNAME);
        }}
        // Numéro de rue : calque ""12""
        else if (n === ""12"") {{
            set(all[j], V_STREET_NUM);
        }}
        // Type de voie : calque ""rue""
        else if (n === ""rue"" || n === ""Rue"") {{
            set(all[j], V_STREET_TYPE);
        }}
        // Nom de rue : calque ""gaston monmousseau""
        else if (n === ""gaston monmousseau"" || n.indexOf(""monmousseau"") >= 0) {{
            set(all[j], V_STREET_NAME);
        }}
        // Pays : calque ""FRANCE""
        else if (n === ""FRANCE"") {{
            set(all[j], V_COUNTRY);
        }}
        // CP + ville : calque ""94200 IVRY SUR SEINE""
        else if (n === ""94200 IVRY SUR SEINE"" || n.indexOf(""IVRY SUR SEINE"") >= 0
              || n.indexOf(""94200"") === 0) {{
            set(all[j], V_ZIP_CITY);
        }}
    }}

    log(""Identite/adresse: "" + V_TITLE + "" "" + V_FULLNAME + "" - "" + V_STREET_NUM + "" "" + V_STREET_TYPE + "" "" + V_STREET_NAME + "" - "" + V_ZIP_CITY);
    log(""Modification terminee"");
}}

try {{
    log(""=== DRIVE START ==="");
    app.displayDialogs = DialogModes.NO;
    var pf = new File(""{P(psdPath)}"");
    if (!pf.exists) throw new Error(""PSD introuvable"");
    var doc = app.open(pf);
    modifyDrive(doc);
    var opts = new PNGSaveOptions(); opts.compression = 6;
    doc.saveAs(new File(""{P(pngPath)}""),       opts, true, Extension.LOWERCASE);
    doc.saveAs(new File(""{P(outputPngPath)}""), opts, true, Extension.LOWERCASE);
    doc.close(SaveOptions.DONOTSAVECHANGES);
    log(""=== DONE ==="");
}} catch(e) {{ log(""ERREUR: "" + e.message); try {{ app.displayDialogs = DialogModes.ALL; }} catch(x) {{}} }}

}})();
";
        }

                private static (string colon, string h) EnsureTime(ProductData data)
        {
            if (!string.IsNullOrWhiteSpace(data.TimeHHMM) && data.TimeHHMM.Contains(':'))
            {
                var p  = data.TimeHHMM.Split(':');
                var hh = p[0].PadLeft(2, '0');
                var mm = p[1].PadLeft(2, '0');
                return ($"{hh}:{mm}", $"\u00e0 {hh}h{mm}");
            }
            var rnd  = new Random();
            var hour = rnd.Next(8, 20).ToString("D2");
            var min  = rnd.Next(0, 60).ToString("D2");
            data.TimeHHMM = $"{hour}:{min}";
            return ($"{hour}:{min}", $"\u00e0 {hour}h{min}");
        }

        private static (string l1, string l2) FormatProduct(string name)
        {
            var words = name.ToUpperInvariant()
                           .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var l1 = ""; var l2 = "";
            foreach (var w in words)
            {
                if      (l1.Length == 0)                     l1 = w;
                else if (l1.Length + 1 + w.Length <= 18)     l1 += " " + w;
                else if (l2.Length == 0)                     l2 = w;
                else if (l2.Length + 1 + w.Length <= 22)     l2 += " " + w;
            }
            return (l1, l2);
        }

        private static string FormatDate09(string dateStr, string timeColon)
        {
            var p   = dateStr.Split('/');
            var ds  = $"{p[0]}.{p[1]}.{p[2][^2..]}";
            var rnd = new Random();
            return $"{ds} {timeColon} {rnd.Next(1000,9999)} 1 {rnd.Next(1000,9999)} {rnd.Next(1000,9999)}";
        }

        private static string GetTemplatePath(
            ShopmiumPdfAutomator.Models.TicketType ticketType =
            ShopmiumPdfAutomator.Models.TicketType.Standard)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var exeDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

            // Nom du fichier PSD selon le type de ticket
            var psdFileName = ticketType switch
            {
                ShopmiumPdfAutomator.Models.TicketType.Leclerc       => "leclerc.psd",
                ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive => "carrefour-drive.psd",
                _                                                      => "template.psd"
            };

            // Chercher dans Resources/ d'abord, puis Desktop, puis répertoire courant
            var candidates = new[]
            {
                Path.Combine(exeDir, "Resources", psdFileName),
                Path.Combine(exeDir, psdFileName),
                Path.Combine(desktop, psdFileName),
                // Fallback template.psd si le PSD spécifique n'existe pas
                Path.Combine(exeDir, "Resources", "template.psd"),
                Path.Combine(exeDir, "template.psd"),
                Path.Combine(desktop, "template.psd"),
                Path.Combine(desktop, "fichier.psd"),
                Path.Combine(desktop, "ticket.psd"),
                Path.Combine(exeDir, "..", "..", "..", "..", "Resources", psdFileName),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", psdFileName),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", "template.psd"),
            };

            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }

            throw new FileNotFoundException(
                "Aucun template PSD trouvé. Placez le fichier PSD dans Resources/.");
        }
    }
}
