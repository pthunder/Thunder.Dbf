# Thunder.Dbf CRM (iPhone sample)

A small contact manager whose data is a dBASE or FoxPro table. It's a .NET MAUI app for iOS, built on this repository's Thunder.Dbf.

**What sets it apart:** tap **Open**, pick a `.dbf` from Files along with its memo (`.fpt` or `.dbt`) and index (`.cdx` or `.mdx`), and that table becomes your contact list. The app works out from the field names which fields hold the name, company, phone, email, follow-up date and notes. So a `CUSTOMER.DBF` from a 1990s FoxPro app works without any setup. Edits go back into the same files, in the same format, with the indexes kept up to date. **Share** sends all three files on, ready to open again in Visual FoxPro, dBASE or Excel.

The rest is a basic CRM:

- search across the name, company, phone and email
- a **Follow-ups due** filter, with overdue dates in red
- a form with a field for every column the table has (memo fields get a multi-line box)
- add, edit and delete; delete marks the record deleted, as dBASE does

If the table has an index but you didn't pick the index file, the app opens it read-only. Thunder.Dbf won't write a table whose index it can't update, because the index would then be wrong for FoxPro or dBASE.

When nothing has been opened yet, the app creates a sample dBASE IV table with 60 contacts, a memo field and an index on the name.

## Run it

You need macOS with Xcode, and the .NET 10 SDK with the iOS workload (`dotnet workload install ios`). The iOS workload has to match your Xcode: if the build says it needs a newer Xcode, either update Xcode or install a workload version built for yours.

```bash
cd samples/Maui
dotnet build -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Then install the app from `bin/Debug/net10.0-ios/iossimulator-arm64/` on a simulator (`xcrun simctl install booted <app>`), or press Run in your IDE.

The app references `src/Thunder.Dbf` directly, so it always runs the library's code from your checkout. It isn't part of `Thunder.Dbf.slnx`, so the repository's CI doesn't build it. CI does run [`samples/Maui.Tests`](../Maui.Tests), which builds `CrmTable.cs` for plain .NET and tests the field guessing, the parsing of text into values, and the read-only rule:

```bash
dotnet test samples/Maui.Tests
```

## Files

| File | What it does |
|---|---|
| `CrmTable.cs` | Opens a table and guesses which fields are which. Lists, reads, saves and deletes contacts. |
| `MainPage.xaml(.cs)` | The contact list: search, filter, Open, Share and add |
| `ContactPage.cs` | The contact form, built from the table's own fields |
| `../Maui.Tests` | Tests for `CrmTable.cs`, run in CI |
| `TablePicker.cs` | Picks several files from Files. On iOS it uses the system picker directly, because MAUI's multi-file picker returns nothing for file types iOS doesn't know. |

The app icon, splash screen, styles and Open Sans fonts come from the .NET MAUI app template. Open Sans is under the SIL Open Font License.
