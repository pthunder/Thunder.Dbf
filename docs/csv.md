# CSV import and export

```
    _____________          _____________
   | a,b,c       |        | A | B  | C   |     ProjectThunder :: Thunder.Dbf
   | 1,x,1997-04 |  --->  |---|----|-----|     A table's shape from a CSV, no Excel round trip.
   |_____________|        |___|____|_____|
```

## From a CSV to a table

```csharp
Console.Write(DbfImport.Plan("customers.csv"));                    // look first
DbfImport.FromCsv("customers.csv", "CUSTOMER.DBF");                 // shape + rows
DbfImport.FromCsv("customers.csv", "EMPTY.DBF", new() { ShapeOnly = true });   // just the shape
DbfImport.FromCsv("customers.csv", "VFP.DBF", new() { Format = DbfTableFormat.VisualFoxPro });   // memos go in VFP.FPT
```

Or from the command line:

```bash
thunder-dbf import customers.csv --plan
```

```
7 columns, 3 rows, delimiter ','
  Customer Name            -> CUSTOMER_N C(14)    text up to 14 bytes
  Balance                  -> BALANCE    N(8,2)   numbers up to 8 wide, 2 decimals
  Zip                      -> ZIP        C(5)     text (values have leading zeros, like ZIP codes)
  Customer Since           -> CUSTOMER_S D        every value is a date
  Active                   -> ACTIVE     L        only yes/no values
  Notes                    -> NOTES      M        text up to 300 bytes, too long for a character field
```

### How columns are shaped

| If every non-empty value is... | The field is |
|---|---|
| yes/no (`T F Y N TRUE FALSE YES NO`) | `L` |
| a date (`yyyy-MM-dd`, `yyyyMMdd`, `M/d/yyyy`, or your `DateFormats`) | `D` |
| a plain number without leading zeros | `N(width, decimals)` sized to fit |
| text up to 254 bytes | `C(longest)` |
| longer text | `M` (memo) |

Codes with leading zeros, like ZIP codes, account numbers and `+0007`, stay text. Headers become valid, unique dBASE names (up to 10 letters, digits and underscores). `Overrides` forces any column's type.

### What it handles

- **Quoting:** RFC 4180 quoted values with commas, doubled quotes and line breaks, including quoted empty values.
- **Delimiters:** comma, semicolon, tab or pipe, detected outside quotes across the whole first record.
- **Encoding:** UTF-8 by default, or a byte-order mark, or `CsvEncoding`. Bytes that aren't valid are an error, never silently replaced.
- **Code pages:** characters the table's code page can't hold are reported with their row and column.
- **Exact text:** text and memos are stored as written, tabs and line breaks included.
- **Big files:** the CSV is streamed twice, once to shape and once to load, so a 77 MB file imports in about 5 seconds with flat memory.
- **Safe output:** the table is built under temporary names and only appears when the import succeeded. If the CSV changes between the two passes, the import is refused.
- **Clear errors:** a bad value is reported by row and column, with HTTP 422 (`InvalidValue`) or 400 (`InvalidCsv`).

## From a table to a CSV

```csharp
DbfExport.ToCsv(table, "customers.csv");
DbfExport.ToCsv(table, "all.csv", new() { IncludeDeleted = true, SpreadsheetSafe = false });
```

- **Format:** UTF-8 with a byte-order mark, so Excel reads accents correctly. Dates are `yyyy-MM-dd`, logicals `T`/`F`, numbers use a `.` decimal point, memos are included in full, and binary values are base64.
- **Spreadsheet-safe by default:** text starting with `=`, `+`, `-` or `@` gets a leading apostrophe, so Excel shows it rather than running it as a formula. Numbers are left alone.
- **Never over table files:** export refuses to write over a `.dbf`, `.dbt`, `.mdx` or any other table, memo or index file, and writes through a temporary file.
