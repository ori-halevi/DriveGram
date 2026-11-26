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
  "skip this version". "Download and install" fetches the release's `DriveGram-Setup-*.exe` and checks it
  against the SHA-256 written in the release notes.
- Publishing is public and outward-facing: **only when the user asks** ("תפרסם"). After a build:
  `publish-release.cmd <version> <notes.md>` (appends the SHA-256, runs `gh release create`). Notes in
  Hebrew, short, what changed for the user.

## Testing aids
- `dotnet test` (100+ tests). Check windows by eye with screenshots before asking the user to build.
