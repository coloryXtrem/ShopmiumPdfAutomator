// === Script de renommage des calques — À exécuter UNE SEULE FOIS dans Photoshop ===
// Ouvrir le PSD, puis : Fichier > Scripts > Parcourir > choisir ce fichier
// Ou : coller dans l'éditeur de scripts PS (Fichier > Scripts > Editeur de script)

(function() {
    function rename(idx, newName) {
        try {
            app.activeDocument.layers[idx].name = newName;
            $.writeln("Renomme [" + idx + "] -> " + newName);
        } catch(e) {
            $.writeln("ERREUR [" + idx + "] : " + e.message);
        }
    }

    rename(7, "date_ticket");
    rename(37, "ref_ticket");
    rename(13, "product_name");
    rename(14, "qty_price");
    rename(15, "amount_line");
    rename(12, "tva_rate_col");
    rename(31, "tva_rate_sec");
    rename(17, "total_payer");
    rename(21, "total_bancaire");
    rename(35, "total_produits");
    rename(32, "tva_total_prod");
    rename(33, "tva_amount_col");
    rename(36, "tva_amount_sec");

    alert("Renommage termine ! Sauvegardez le PSD (Ctrl+S) puis relancez l'application.");
})();
