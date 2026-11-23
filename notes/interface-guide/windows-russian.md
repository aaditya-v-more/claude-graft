# Windows in Russian

[Back to interface.md](../interface.md)

Windows localizes the way the Mac does: the English on screen is the key, and
Russian comes from `windows/ClaudeGraft.Core/Localization/ru.json`, which is
built into the core assembly so the app and the suite read the same file. `L10n`
shows Russian when Windows' display language is Russian and English otherwise;
`GRAFT_LANGUAGE=en` or `ru` overrides it for one run, and the suite pins English
and steps into Russian with `L10n.Speaking`. Code calls `L10n.Text`, `Format`,
`Plural` or `List`, and XAML sets `local:Localized.Text` and its siblings in
place of the plain attribute. A count's Russian sits under its plural English as
one|few|many. The suite fails on a string missing from the catalog, a translation
that loses a placeholder and a bare English attribute in XAML. Product names,
diagnostic notes, the state report and the debug window stay in English, as
does the subject of the support email, which is written to the maintainer.
