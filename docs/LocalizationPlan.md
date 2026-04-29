# Plan d'implementation localisation

## 1. Base technique
- Ajouter un code langue UI persiste dans `AppState` (`UiLanguageCode`).
- Appliquer la culture au demarrage (`Program`) via `UiLanguageService`.
- Garder un fallback robuste: langue choisie -> langue neutre -> anglais.

## 2. UX de selection de langue
- Ajouter un champ `Language` dans `SettingsForm`.
- Afficher les noms de langues dans leur nom natif (`CultureInfo.NativeName`).
- Inclure `System default` (auto-detection OS), sans drapeau.
- Activer le mode RTL automatiquement pour les langues RTL (arabe/farsi).

## 3. Couverture linguistique initiale
- Catalogue de langues ajoute (Europe + extra demandes): `Core/UiLanguageCatalog.cs`.
- Langues extra ajoutees: russe, ukrainien, japonais, chinois simplifie/traditionnel, coreen, arabe, farsi.
- Ajout de l'hindi comme langue majeure supplementaire.
- Ajout de l'indonesien (`id`) et du bengali (`bn`) comme langues majeures supplementaires.

## 4. Migration des textes UI
- Phase 1: textes communs et settings critiques.
- Phase 2: top bar/tray/menu contextuel Evernote. (en cours: branchement i18n + refresh dynamique)
- Phase 3: dialogs et messages runtime (erreurs/export/sync).
- Phase 4: validation linguistique humaine pour les langues non maitrisees.

## 5. Validation
- Test fonctionnel: changement langue, persistence, reouverture app.
- Test RTL: alignement et lecture pour arabe/farsi.
- Test longueur textes: allemand, finnois, hongrois.
- Test import/export settings: conservation de `UiLanguageCode`.
