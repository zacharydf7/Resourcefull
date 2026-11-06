# Names

Container and resource names follow the same rules on every provider:

- **Case-sensitive.** `Report.pdf` and `report.pdf` are different resources, and `Docs` and `docs` are different
  containers, whatever the SQL Server's collation.
- **Length:** a container name has 1 to 128 characters (`ResourceNames.MaxContainerNameLength`), and a resource name
  1 to 256 (`ResourceNames.MaxResourceNameLength`).
- **No whitespace at either end.** SQL Server ignores trailing spaces when it compares strings, so names that differ
  only there are rejected rather than treated as the same name.
- **No control characters, bidirectional controls or unpaired surrogates**. Control characters, such as
  line breaks and tabs, break headers and logs. Unicode's bidirectional controls (U+061C, U+200E and U+200F, U+202A to
  U+202E, and U+2066 to U+2069) can make a name display as another: a name made of `invoice`, U+202E and `fdp.exe`
  shows as `invoiceexe.pdf`. An unpaired surrogate isn't valid text, so JSON and HTTP replace it, and an export would
  change the name.

Any other character is allowed, including `/`, so `2026/10/report.pdf` is a valid resource name. Containers don't nest,
though: the slash is just part of the name. Joiners (U+200C and U+200D), which some scripts and emoji need, are allowed
too.

The rule on these characters applies only to names a write creates: uploads, the names copies, moves and renames go
to, and containers being created or imported. A name stored by a release before 4.0 with these characters inside it can
still be read and deleted, and an export of it can be imported; a resource with one can be moved or renamed to a name
that follows the rule. `CreateContainerIfNotExistsAsync` still finds a container with such a name, and checks
the rule only when it creates one.

Every method that takes a name checks it before contacting storage, and throws `ArgumentNullException` for `null` and
`ArgumentException` for a name that breaks the rules, so an API can turn that into a 400. To check a name yourself
first, for example to validate a whole request before acting on any of it, call `ResourceNames.IsValidContainerName`
or `ResourceNames.IsValidResourceName`.

Names appear in URIs and file names that you build from them, so escape them there; see
[URI providers](uri-providers.md) and [ASP.NET Core](aspnet-core/index.md).
