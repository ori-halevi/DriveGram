# DriveGram: project guide for agents

A drive letter in "This PC" whose files live encrypted in the user's Telegram channels.
WinFsp 2.1 + C# (.NET 10): `src/DriveGram.Core` (engine, index, Telegram), `src/DriveGram.Host`
(WinFsp file system, sessions), `src/DriveGram.Tray` (tray app, wizard, windows), `src/DriveGram` (CLI),
`src/DriveGram.Shell` (C++ Explorer extension: overlay marks + right-click menu), `installer/` (Inno Setup),
`tests/DriveGram.Tests` (xUnit, `dotnet test`).

## Working with the user
- Reply in Hebrew, with little technical jargon.
- Commit messages: no Claude / Co-Authored-By attribution. Never commit `.env` or personal details.
- **Build only when the user says "תבנה"** (`build-installer.cmd` → `dist\DriveGram-Setup-<ver>.exe`).
- Code changed after a build → raise the version first, in all four places: `build-installer.cmd`,
  `installer/DriveGram.iss`, `src/DriveGram/DriveGram.csproj`, `src/DriveGram.Tray/DriveGram.Tray.csproj`.
  1.y.0 for features, 1.y.z for fixes.
- No cloud actions (Artifacts, Google Drive, …) unless asked. Cross-project lessons live in
  `C:\Users\OH\Desktop\MyScripts\CLAUDAI\lessons-learned.md`: propose additions, never edit without approval.

## Design decisions (agreed; don't re-open unprompted)
- Standalone (own index, own Telegram session), not a client of InfiDrive's server. InfiDrive's git log and
  docs hold the hard-won Telegram-storage lessons.
- Journal in Telegram from day one (alternating multi-PC with a lease; simultaneous later). Storage-format
  constants are permanent. Never journal a ContentOp before its chunks are stored.
- Not wanted: local encryption at rest, re-keying, one drive striped/mirrored over several accounts (deferred).
- Network drive is the wizard's default drive type; regular disk is a choice.

## How the windows are meant to feel (agreed 2026-11-26; this is the user's design)
- **One home**: the main window with four equal, full-width tabs — חשבונות / כוננים / העברות / הגדרות — is
  where the user always comes back to. It opens on "חשבונות".
- **Every other window does ONE job and then closes**, handing the user back to the main window (adding an
  account closes when the account is in; it does not go on to drives).
- **A setting lives in exactly one place.** Language, look and the daily cap: only the Settings tab (the tray
  menu's "הגדרות…" just opens it). What belongs to one drive (letter, name, type, its space on this PC) lives
  in that drive's own windows, never in Settings.
- **Nothing important hides behind a link.** In "העברות" the legend and the help stay visible, with the full
  explanation on hover.
- The tray icon: one click = the menu, two = the window on "העברות". The icon artwork itself does not follow
  the look (it sits on the Windows taskbar); its menus do.

## The look (light / dark / rose)
- `src/DriveGram.Tray/Setup/Theme.cs` holds three palettes and the colour SLOTS (`Slot.Window`, `Slot.Card`,
  `Slot.Muted`…). **Never write a literal colour in UI code** — use `Theme.X` or `ctl.Wears(Slot.X, Slot.Y)`,
  or the look cannot change while the program runs. (The only literals left on purpose: the white behind a QR
  code, and white on the red "delete for ever" button.)
- `Theme.Apply(form)` dresses a whole window; `Ui.Prepare` hooks it to `Form.Load`. A control nobody dressed
  has its slot GUESSED ONCE from the colour it has then — which is why `Theme.Set` applies the old look first
  (to record the slots) and only then the new one.
- `Application.SetColorMode` (what makes Windows' own scroll bars, drop-downs and title bars dark) must be
  called AFTER `Application.SetCompatibleTextRenderingDefault`, and only takes full effect on windows opened
  after it: the Settings tab says so in one line.
- A list's column headers are a window of Windows' own inside it: `ThemePaint.List` asks for its dark styles
  (`DarkMode_Explorer` / `DarkMode_ItemsView`). Painting them ourselves was tried and came out in pieces —
  in a mirrored (Hebrew) list Windows repaints only the part it believes changed.
- `ThemedTabs` replaces `TabControl` (its tabs are painted by Windows and cannot be recoloured).
- Screenshots: `drivegram-tray --snapshot-main <dir> --theme light|dark|rose --he|--en` renders every tab of
  the main window, off screen, with sample data. **Rows and column headers of a list never appear in those
  pictures** (Windows paints them, and they stay out of a window's own drawing) — check lists on a real run.

## Things that are easy to get wrong
- **Explorer marks**: the overlay names are machine-wide (HKLM, written once by an elevated `reg.exe` at
  install); the COM classes are per user (HKCU). Explorer shows only 15 overlays, sorted by name: ours start
  with three spaces, and the "not in Telegram yet" ones (`   DriveGram1Upload`, `   DriveGram2Paused`) sort
  first. Explorer reads the names only at sign-in, and keeps the old DLL loaded until then: every version's
  DLL has its own file name, and the installer must never close Explorer (`CloseApplicationsFilter=*.exe`).
- A mark must never say "saved" before it is: a file counts as in Telegram only when it is not pending AND
  has no op left in the outbox (`ContentEngine.IsWaitingToUpload`).
- **Preview windows** (`cache\v`): what explorer/dllhost read for thumbnails is kept apart, survives "free",
  and does not count as the file being here. The rules for what counts as a preview (window limits, a 4 MB
  run = an Explorer copy) are guesses until checked against the log lines "preview by <program>: …" on a
  real PC (the Windows file cache changes read sizes).
- Explorer calls into the extension on its own threads: every call is time-limited (150 ms) and cached.
  Anything the tray answers per Explorer file must be cheap (indexed queries; the DB has one lock shared
  with the file system).

## Updates and releases
- Installed copies ask GitHub (`ori-halevi/DriveGram`, releases/latest) once a day whether a newer version
  is out (`src/DriveGram.Tray/Updates.cs`): a menu line, a bar in the main window, one notice per version,
  "don't offer this version", and "Check for a new version" in the icon's menu. "Download and install"
  fetches the release's `DriveGram-Setup-*.exe` from this repo's release downloads only, and only when its
  SHA-256 is known: GitHub's own asset `digest` first, else the `SHA256 (<file>): <hex>` line that
  publish-release.cmd appends to the notes. Without either, only the download page is offered.
- The version that ships is `VERSION` in build-installer.cmd (passed to dotnet and Inno); keep the csproj and
  .iss defaults in step. build-installer.cmd records the commit it built (`dist\*.commit`), and
  publish-release.cmd refuses a mismatch, uncommitted/untracked files, or a commit not yet on GitHub.
- **Deferred on purpose (2026-11-26): signing releases.** Today the installer is trusted because it comes
  from this repo's release downloads over TLS and matches GitHub's own SHA-256 of it. Whoever controls the
  GitHub account (a stolen login or token) could therefore publish an installer that every copy offers.
  The user decided not to sign for now, and to do it **once the app has many users**: an offline Ed25519 key
  (kept off GitHub and off the build PC), the public key built into the tray, a signature asset next to the
  installer, and "Download and install" refusing without a valid one. The public key is permanent once
  shipped (losing the private key breaks one-click updates for every installed copy): raise this with the
  user before the first signed release. Until then, GitHub two-factor sign-in is the protection.
  Also deferred to the same moment (the owner's decision, 2026-11-26): turning on **"Immutable releases"**
  in the repo settings (a published release's files can then never be replaced, not even from the owner's
  account). The owner wants releases editable until the app is public and widely used; publish-release.cmd
  already works either way.
- Publishing is public and outward-facing: **only when the user asks** ("תפרסם"). After a build:
  `publish-release.cmd <version> <notes.md>` (appends the SHA-256, runs `gh release create`). Notes in
  Hebrew, short, what changed for the user.

## Testing aids
- `dotnet test` (100+ tests). Check windows by eye with screenshots before asking the user to build.
