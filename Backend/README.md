# M1 authentication configuration

Production has no default users. Configure `Auth:Users` entries containing `Username`,
`PasswordHash`, and `Roles` (`learner`, `reviewer`, or both). Environment example:
`Auth__Users__0__Username`, `Auth__Users__0__PasswordHash`, `Auth__Users__0__Roles__0`.
Plaintext `Password` is rejected outside Development. Development-only credentials live
in `Palabras/appsettings.Development.json`; never copy them into production.

Generate an ASP.NET PasswordHasher hash with:

```sh
dotnet run --project Backend/Palabras -- --hash-password
```

Supply the password on stdin, followed by Enter (input may be visible in the terminal).
Prefer a secure stdin source. Do not pass passwords as command-line arguments or commit them.
Store the printed hash in production configuration.

`DataProtection:KeysPath` defaults to `/data/keys` in Production. Mount persistent writable
storage there, accessible to the container's `app` user. Keys are not encrypted at rest;
restrict directory access and protect backups. An unset/unwritable path falls back to
process-local keys with a warning: app keeps running, but cookies will not survive restart.
All instances use the fixed data-protection application name `Palabras`.
