# EnvDeck — notes for coding agents

Windows environment-variable profile manager: `src/EnvDeck.Core` (logic), `src/EnvDeck.Cli` (`envdeck`),
`src/EnvDeck.App` (WPF, Fluent, MVVM), `tests/EnvDeck.Core.Tests` (xUnit). .NET 10.

## Safety first (read before running anything)

- **Never touch the real environment.** Every manual or UI test must set `ENVDECK_SANDBOX=1`
  (writes to `HKCU\Software\EnvDeck\Sandbox`) and `ENVDECK_HOME=<temp folder>`. Delete both afterwards.
- Unit tests use `TestDeck` (in-memory store); keep new Core tests on it.
- Do not try to add Defender exclusions or disable security policy. New unsigned exes are blocked outside
  `D:\TMS`, so Debug builds use `UseAppHost=false`: run `dotnet EnvDeck.App.dll`, not an exe.
- If the user's EnvDeck is running from `bin\Debug`, build to a temp folder (`dotnet build -o D:\Temp\x`).
  Never kill their process.
- Do not commit, push, tag or open PRs unless asked.

## Checks that must pass

```
dotnet build EnvDeck.slnx          # 0 warnings, 0 errors
dotnet test tests/EnvDeck.Core.Tests
```

Every key in `src/EnvDeck.App/Localization/Strings.cs` needs English **and** Vietnamese, no duplicates,
no key used in XAML/code that is missing from the table.

## Code conventions

- Core: no UI types. Behaviour changes get a test. Writes to the environment go through `DeckService`
  (auto backup, apply journal in `state.json`, elevation check for Machine scope).
- App/MVVM: CommunityToolkit.Mvvm 8.4 with **partial properties** (`[ObservableProperty] public partial T X { get; set; }`),
  `[RelayCommand(CanExecute = ...)]`.
- All user-visible text goes through `Loc`: `{l:Tr Key}` in XAML, `L.T("Key", args)` in code; text built
  in code must be refreshed in the view model's `RefreshTexts()` on language change.
- Dialogs: use `Dialogs.Confirm/ConfirmDanger/Ask/Info/Error`, never `MessageBox`.
- DataGrid cells use the shared `CellText` / `CellCombo` styles (App.xaml). Do **not** set a `RowStyle`
  `BasedOn="{x:Type DataGridRow}"` — it rendered rows with a white background in Fluent theme; style cells instead.
- WPF Fluent gotchas for grid cells (all measured on screen, dark theme; re-check light after changing them):
  - Combo columns: never use `DataGridComboBoxColumn` — it needs 3 clicks (select row, edit mode, open) and its
    editing ComboBox is taller. Use a `DataGridTemplateColumn` with a `ComboBox Style="{StaticResource CellCombo}"`
    (flat template: no box, chevron, popup with theme brushes). It takes its text colour from the cell and goes
    dark on a selected row, because the theme colours the text per state and that is unreadable there.
  - Text cells: set `EditingElementStyle="{StaticResource CellTextEdit}"` next to `ElementStyle`. It matches the
    TextBlock's left edge and height so text does not jump, covers the whole cell (negative margin, square corners
    via `ControlCornerRadius`, no accent underline) and uses the opaque `SolidBackgroundFillColorBase`: the theme's
    input fill is translucent, so on a selected row it turned purple.
  - Theme brushes are overridden through `Style.Resources` with the theme's own keys (`TextControlBackground*`,
    `ComboBox*`, `ControlCornerRadius`); list them by searching `PresentationFramework.Fluent.dll` for the key text.
    `TextBox`/`Control` have no `CornerRadius` property.
  - Inside a custom template, give inner `ToggleButton`/`Button` `Style="{x:Null}"`, or the implicit theme style
    overrides foreground and padding.
  - Bindings inside a cell template to `ToggleButton.IsChecked` (and other controls that edit the row) must say
    `Mode=TwoWay, UpdateSourceTrigger=PropertyChanged`; relying on the default left the view model unchanged
    (the eye button toggled but the text stayed masked).
  - A templated cell editor does not take focus by itself: give its TextBox a `Loaded` handler that focuses it.
  - Moving a row (`ObservableCollection.Move`) drops keyboard focus: re-focus the moved row's cell (`FocusItem`).
  - Check selected rows with a real mouse click (UI Automation `SetFocus` does not select the row, so colours
    differ), and measure pixels/heights instead of judging screenshots by eye.
- Font is Segoe UI. Keep the existing comment density: short `///` summaries that explain *why*.
- Keep keyboard/accessibility parity (e.g. Alt+Up/Down as the alternative to dragging).

## Secrets

- `SecretNames.Looks` guesses from a name; the user's override lives in `settings.json` (`secretNames`, `plainNames`).
  Never put a value where the UI can show it without going through `Secrecy.Show` (tables, search, compare, preview,
  conflict dialogs, status text). Reveal state is per row and is not an edit of the profile.
- Bundles: `BundleCrypto` (AES-256-GCM, PBKDF2-SHA256 600k) works in memory, so a readable zip never reaches the disk.
  Passwords are normalized (FormKC) before use. Do not log or echo them; the CLI reads them without echo.

## Testing the UI

Drive the window with UI Automation against a sandboxed instance; prefer reading cell text over screenshots.
Only capture a screenshot after confirming EnvDeck is the foreground window (never other apps).

## Releasing

Bump `<Version>` in `Directory.Build.props`, tag `vX.Y.Z`, push. `.github/workflows/release.yml` tests, compiles
(`scripts/publish.ps1 -PublishOnly`), optionally signs through SignPath (`SIGNPATH_ENABLED`, off: foundation
application declined), packages (`-Prebuilt`), attests provenance and publishes; the `winget` job needs
`WINGET_ENABLED` (only after the first winget-pkgs PR is merged). Scoop's `bucket/EnvDeck.json` is updated by hand
after each release. Details in README "Publishing to winget". No MSIX: registry writes would be virtualized.

## Commits

Conventional Commits (`feat(app): ...`), body explains *why*, atomic commits, verify each builds.
Add the `Co-Authored-By` trailer for the agent that wrote it.
