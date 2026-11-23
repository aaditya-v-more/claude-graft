# Opening a profile

Every route that opens a Claude, and the rules that keep two of them off one chat store. Read this before changing `Graft.open`, `Graft.run`, `ProfileOpener`, `ChatConflict`, the launcher or `Installer`.

## Apps and folders this app may touch

**Never touch an app Graft did not create.** Bundles are identified by the
`graft.json` they carry, never by name. `Claude` and `Claude Graft` are reserved
names. This rule exists because an earlier version matched on name and deleted
`/Applications/Claude.app`.

**Profile folders are validated.** One plain component inside Application
Support, never `Claude`, never a path. Without this, typing `Claude` into the
folder field pointed a shortcut at Claude's own profile.

The reserved names are compared ignoring case, because a Mac's usual volume
does. `claude` passed the old check and was Claude's own profile under another
spelling; `claudegraft` passed it too and put a profile in Graft's own state
folder, and deleting that profile would have taken every pair, baseline and
record with it. `deleteProfile` refuses Graft's folder outright, by path and by
name, since a path spelt in another case need not resolve to the same string,
and `ShortcutStore.delete` counts two shortcuts as sharing a profile when their
folders match ignoring case or resolve to the same place, not when the text is
identical. The one place that still lets `Claude` through, the manual-update
library for the main profile, asks the same way, so a profile reached through a
lower-case path is not refused there.

**One profile folder, one shortcut.** A new shortcut's folder comes from its
name, and a shortcut keeps its folder when it is renamed, so "Claude 2" renamed
left Claude-2 behind a name nobody held. The next new shortcut was offered
"Claude 2", took Claude-2, and opened already signed in to the renamed one's
account; deleting it and adding another only offered the same name again, and
renaming an existing shortcut to make a new account kept that shortcut's login
for the same reason. `uniqueName` now passes over a name whose folder another
shortcut uses, or that is still on disk from a shortcut that is gone, since
that folder holds whoever was signed in there last. `Installer.install` refuses
a new shortcut, or one whose folder is changing, on a folder another shortcut
uses, and says whose it is. A shortcut already on its folder stays updatable
whatever an older list holds, so a pair left sharing one by an earlier version
is untangled by moving one of them, not by refusing both.

**The name chooses the folder until a folder is typed.** On a shortcut not yet
created the folder follows the name, and only the folder field itself stops
that. An `onChange` of the folder used to, and it also saw the folder the name
had just chosen, so the folder followed the first keystroke and no other.
Backspacing the 2 of "Claude 2" passes through an empty name, whose folder is
Claude-Profile, and on a Mac with one the new shortcut stopped there: it showed
that account's plan usage and offered its Start Session, and the page, looking
its app up by the folder alone, took that shortcut's app for its own, so Open
started the other account's Claude. The name's folder is now `suggestedFolder`,
never one another shortcut has or one still on disk, numbered past them
instead. The name never moves a created shortcut's folder; that is decided by
`installedName`, which is in the list from the start, not by the app lookup,
which arrives after the page opens. The page looks its app up with the whole
list and its figures by shortcut, so one an earlier version left on another's
folder reads as not created, shows no figures, offers Discard, says whose
folder it is, and takes a folder of its own at the next key.

**A name an app was created as is that app's.** A shortcut's app is
`<installedName>.app` until it is updated, whatever it is called now. Looked up
by name, a shortcut not created yet and called the same, on the same folder,
was handed that app, read as created, and Start Session, Open and Delete all
reached the other account; created under that name, a new shortcut was written
over it. `installedBundle` skips a name another shortcut was created as unless
it is this shortcut's own, `uniqueName` does not offer one, and
`Installer.install` refuses to write over another shortcut's app. The one
exception is an app this shortcut was itself created as that opens its own
folder, since an earlier overwrite could leave two shortcuts with one installed
name; found by name or by folder alone is not enough, because another's app
still opening a folder its list entry has left matches both. Only a created shortcut owns an app, so
for a created shortcut whose folder is shared only with others not created
yet, the app found by its folder is its own, renamed in Finder.

**Windows keeps the same rule** in `ShortcutStore`: `UniqueName` passes over a
name whose folder is not free, `SuggestedFolder` is what the editor's name
writes into the folder box, and `Conflict` refuses a new or moved Claude on a
folder another one has. It also refuses a name another Claude has, compared
ignoring case, because both would write the same desktop `.lnk` and the second
would open the first's profile.

## Topic guides

- <a id="opening-revealing-and-the-shortcut-bundle"></a><a id="recognising-a-running-claude"></a>[Launching and recognizing profiles](opening-profiles-guide/launching-and-recognizing-profiles.md)
