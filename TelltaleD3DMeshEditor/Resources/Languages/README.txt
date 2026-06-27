Telltale D3DMesh Editor - Translations / Traduções / Traducciones
================================================================

This folder holds the tool's UI languages. Each language is a single .json file
(for example en.json, pt-BR.json, es-419.json). The tool scans this folder on startup,
so you can add a new language WITHOUT recompiling the tool - just drop a new
<code>.json file here and restart.

You can change the active language in the tool under: Settings -> Language.


How to add a new language
-------------------------
1. Copy "en.json" and rename it to your language code, e.g.:
     fr.json   (French)
     de.json   (German)
     it.json   (Italian)
     ja.json   (Japanese)
   Use a short code. A plain language code ("fr") is fine; a region-specific code
   ("pt-BR") is also fine and will be auto-detected for that exact Windows locale.

2. Open your new file in any text editor (it is UTF-8 JSON) and edit the "_meta"
   block at the top:
       "code":        must match the file name (without ".json").
       "nativeName":  the language name written in that language - this is what
                      appears in the Settings language picker (e.g. "Français").
       "englishName": the language name in English (e.g. "French").
       "author":      your name / handle (optional).
       "baseVersion": the tool version you translated against (optional).

3. Translate ONLY the values (the text on the right of each ":"). Leave every
   key (the text on the left) exactly as it is - the keys are how the tool finds
   each string.

       "toolbar.settings": "Settings",          <-- key       value
                            ^^^^^^^^ keep        ^^^^^^^^ translate this


Important rules
---------------
- Keep the placeholders intact: {0}, {1}, {2}, ... and {1:g}. They are replaced at
  runtime with file names, counts, versions, etc. Keep the same placeholder numbers
  in your translation (you may reorder them to fit your grammar). Example:
      "status.opened": "Opened: {0}"   ->   "status.opened": "Aberto: {0}"

- Keep \n (line breaks) and \" (quotes) as written.

- Do NOT translate technical terms, file names, extensions, shortcuts or proper
  names. Keep them exactly:
      GLB, GLTF, DXT, UV0, EOF, V25, BTTF, ERTM, AA
      .d3dmesh, .skl, .d3dtx, .ttarch, .ttarch2
      WASD + Q/E
      Discord Rich Presence, GitHub, Blender
      Game/studio and people names (Telltale Games, Skunkape Games, Michonne,
      Back to the Future, Tales from the Borderlands, etc.)

- Missing a key is OK: if your file does not contain a key, the tool falls back to
  the English text for that one string, so the tool never breaks. en.json is the
  reference for the full, up-to-date key list.

- Save the file as UTF-8. Keep it valid JSON (a missing comma or quote will make the
  tool ignore the file). You can paste it into any online JSON validator to check.


That's it. Restart the tool, open Settings -> Language, and your language will be in
the list. Thanks for contributing! / Obrigado por contribuir! / ¡Gracias por contribuir!
