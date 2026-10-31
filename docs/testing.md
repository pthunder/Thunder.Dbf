# Testing

```
     _____________                   ProjectThunder :: Thunder.Dbf
    | your data   |                  Real files from real apps, a key-for-key index oracle,
    |  goes here  |                  and a way to test against your own data without
    |_____________|                  ever committing it.
```

## Running the tests

```bash
dotnet test Thunder.Dbf.slnx
```

CI runs them on Windows, Linux and macOS. A few tests depend on the platform. For example, the lock tests use a second process on Linux and a second handle on Windows, and on macOS they check that locks honestly report as not enforced.

## Fixtures

`tests/fixtures` holds real files from dBASE III/IV, FoxPro 2, Visual FoxPro, the US Census Bureau and a Siemens STEP 7 project. Each source, its pinned commit and its license are listed in [tests/fixtures/SOURCES.md](../tests/fixtures/SOURCES.md).

**Fixtures must be openly licensed and contain no real people's data.** Commercial "sample" databases and customer data never go in the repository, and the editorial check refuses data files anywhere outside `tests/fixtures`.

## Testing against your own data

Point `THUNDER_TEST_DATA` at a private copy of a legacy folder:

```bash
THUNDER_TEST_DATA=~/legacy-copy dotnet test --filter LocalDataTests
```

`LocalDataTests` then:

1. runs the handshake and reads every record of every table
2. rebuilds every index tag from its table and compares it key for key
3. makes random appends, updates, deletes and recalls on a scratch copy, then checks every tag again
4. rebuilds the indexes and checks once more

Your data is only read; the writes happen on a temporary copy, which is deleted afterwards. Nothing about it is committed.

## The index oracle

Index tests don't compare against stored expected bytes. They rebuild what each tag *should* hold, by evaluating its key and `FOR` expressions against every record, and compare that with what the index *does* hold. That catches mistakes in both reading and writing, on any data.

## Proving tests can fail

For format and index code, break the implementation on purpose (a wrong separator key in a node split, a wrong exponent bias in numeric keys) and confirm the tests fail before trusting a green run. The editorial guidelines ask this of every change to that code.
