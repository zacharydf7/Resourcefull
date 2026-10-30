# Names

Container and resource names follow the same rules on every provider:

- **Case-sensitive.** `Report.pdf` and `report.pdf` are different resources, and `Docs` and `docs` are different
  containers, whatever the SQL Server's collation.
- **Length:** a container name has 1 to 128 characters (`ResourceNames.MaxContainerNameLength`), and a resource name
  1 to 256 (`ResourceNames.MaxResourceNameLength`).
- **No whitespace at either end.** SQL Server ignores trailing spaces when it compares strings, so names that differ
  only there are rejected rather than treated as the same name.

Any other character is allowed, including `/`, so `2026/10/report.pdf` is a valid resource name. Containers don't nest,
though: the slash is just part of the name.

Every method that takes a name checks it before contacting storage, and throws `ArgumentNullException` for `null` and
`ArgumentException` for a name that breaks the rules, so an API can turn that into a 400. To check a name yourself
first, for example to validate a whole request before acting on any of it, call `ResourceNames.IsValidContainerName`
or `ResourceNames.IsValidResourceName`.

Names appear in URIs and file names that you build from them, so escape them there; see
[URI providers](uri-providers.md) and [ASP.NET Core](aspnet-core/index.md).
