# 0018 — Accounts in a library nobody owns

**Status:** accepted, 2026-10-19

## Context

The library is files on a disk, and everything Fonoteca knows about them is one
shared catalogue: no user is in it and none should be. But some things are a
person's: a heart, a Subsonic app's sign-in. Until now `/api` and the web
client had no sign-in at all, `/rest` checked one `Fonoteca:SubsonicPassword`
and ignored the name, and `Loves` had no owner. ADR 0012 said in as many words
that an authenticated `/rest` beside an open `/api` is a front door next to an
open window.

The owner's words: "a single library and there are no users in that cause it
is file based and shared. However I do need proper accounts for things like
playlists and OpenSubsonic". Their answers, one question at a time:

- one account signs in to the web client and to Subsonic, and `/api` closes;
- every account is equal — "roles may come later" (*superseded 2026-10-21:
  roles, below*);
- playlists stay everyone's, static and smart;
- hearts are per account, and those given before accounts are the first one's;
- an accounts page manages them, the first visit makes the first account, and
  removing one removes its hearts;
- "follow the full standard": ASP.NET Core Identity for the accounts, and
  OpenSubsonic's whole authentication — `p`, `enc:`, `t`+`s` with the same
  name and password, *and* the `apiKeyAuthentication` extension.

## Decision

**ASP.NET Core Identity, without its role tables** (`IdentityUserContext<Account, Guid>`,
`Microsoft.AspNetCore.Identity.EntityFrameworkCore`). The hash, the lockout and
the security stamp are the framework's rather than a copy. Its cookie,
`fonoteca`, is the web client's session; a fallback authorization policy makes
every route need a signed-in account unless it says otherwise — `/health`,
`/openapi`, the sign-in endpoints under `/api/account`, and `/rest` and `/mcp`,
which keep guards of their own. An API route answers 401, never a redirect.
A reset password or a removed account ends that account's other browser
sessions within a minute (`SecurityStampValidatorOptions.ValidationInterval`,
not Identity's thirty).

**The first visit makes the first account.** While none exists,
`GET /api/account/setup` says so and the web client shows "Make your account"
instead of the sign-in form; `POST` makes it, signs in, and claims every heart
with no account. Once one exists setup answers 409. The last account — since
roles, the last admin — cannot be removed, or whoever reached the port next would make the first one again.

**Every account may do everything**, including managing the others
(*superseded 2026-10-21 by roles, below*). The
journal names the account that acted (`AccountCallerContext`, the
`NameIdentifier` claim) where it used to say `owner`; work with no request
behind it is `system`, as before, and `/mcp` is still `agent`. The decision
endpoints' person/agent split (rule 4) is unchanged: an account is a person.

*Amended 2026-10-21: roles*, for a Subsonic client that hides what an account
may not do. The owner: "Start by making every account admin, built the
accounts fully to follow the opensubsonic standards", and that the web follow
the roles too. An account holds OpenSubsonic's twelve (`AccountRoles`, one
integer column; Identity's role tables stay unused), and every account from
before them was given each role naming something Fonoteca does: admin,
settings, download, upload, playlist, cover art and stream. The first account
gets the same; one added without roles gets `createUser`'s defaults, settings
and stream. They bind on the web as on Subsonic. **Admin** is accounts, every
pass, the file manager's trash and move, Identify's decisions, follows and
monitors, fingerprints, tag writes — the tag editor and Undo among them, since
both write files at once — and Qobuz, as the option the owner chose put it.
**Cover art** ("change cover art and tags") is covers, artist pictures and a
person's corrections to an album or artist, which reach the files only through
a tag write. **Upload** is the file manager's upload; **settings**, or admin,
one's own password and API keys; **stream** or **download** a song's bytes,
since one URL plays and downloads. **Playlist** binds nothing — "changing this
role has no effect", the protocol's words — so playlists stay everyone's;
comment, podcast, jukebox, share and video conversion name nothing here. Each
route names its role (`RequireRole`), and the web client hides what the
signed-in account may not do. The roles are claims (`AccountClaims`), made again
at each security stamp check, so a change reaches a signed-in browser within a
minute without signing it out. The last admin can be neither removed nor
demoted, which also keeps the last account.

**One password for both clients, which Subsonic forces to be recoverable.**
`t = md5(password + salt)` can only be checked by computing it, so beside
Identity's hash sits `Account.SubsonicPassword`, the same password protected by
ASP.NET Core Data Protection (application name `Fonoteca`, so a moved checkout
can still read it). Both are set in the same save, always through
`AccountEndpoints.SetPasswordAsync`. Subsonic's `p` and `enc:` are checked
against the copy too, so a cover request does not run PBKDF2. The key ring is
in Data Protection's default place, `~/.aspnet/DataProtection-Keys`, not in the
database, so a database backup alone holds nothing usable — and losing the ring
signs every browser out and leaves every Subsonic password unreadable until it
is set again. Wrong Subsonic passwords count towards Identity's lockout (five,
then five minutes), the same lockout as the web form.

**OpenSubsonic's API keys** (`apiKeyAuthentication`, version 1, advertised).
Keys are 32 random bytes made by the server, shown once, listed by name and
revoked on the accounts page, never expiring — all the extension's own terms.
Only their SHA-256 is kept: a key is random, so a slow hash buys nothing, and
the lookup has to be by the hash. `apiKey` must come alone: any of `u`, `p`,
`t` or `s` beside it, even empty, is error 43; an unknown key is 44.
`tokenInfo` names the key's account. Error 42 is never answered: the spec
keeps it for a mechanism the server does not implement, and every one is
implemented. Symfonium sends keys; most clients do not yet, which is why the
password forms stay.

**A heart is its account's**, by an EF global query filter on `Love` keyed on
`FonotecaDbContext.AccountId`, which the Subsonic guard sets per request — so
no query can see another account's hearts by forgetting a `Where`. The two
places hearts move for everybody (a merged recording, an album filed again)
read past the filter and carry each account's hearts separately.
*Amended 2026-10-22:* an account's tags and playlist folders (ADR 0012) are
filtered the same way, a song's tags carried with its hearts.

**Playlists stay everyone's**, and a Subsonic client is told the signed-in
account owns each one, so it lets them be edited. *Amended 2026-10-22:* where
each account keeps them, in folders of its own, is that account's.

**The web client reaches the API on its own origin.** Vite proxies `/api` and
`/hubs` to the API in development, so the session cookie is first-party
whatever name the page is opened by. `AccountGate` shows the library at once
and swaps in the sign-in form on the first 401 from any request.

## Consequences

- Every Subsonic app signs in again, with an account's name and password or a
  key. `Fonoteca:SubsonicPassword` is gone, and `/rest` no longer answers 404
  when unset: it is always there, refusing with error 40 until an account
  exists.
- Anything that called `/api` without a session — a `curl` in a script or a
  skill — now gets 401. `/mcp` is unchanged: its token, acting as the agent.
- The Data Protection key ring is a file of keys on disk, unencrypted at rest.
  It guards the Subsonic copies of the passwords and the session cookies.
- The `AccountId` on a heart is nullable only for hearts given before there
  were accounts; the first account claims them all.

## When to revisit

**When somebody needs to be kept from something** — roles. The fallback policy
is where they go. *Amended 2026-10-21:* they came, above; the fallback stays
"signed in" and each route that changes something names its role.

**When playlists should be a person's.** They are files in a shared folder
that other programs read; an owner is a column, a filter like the hearts', and
a question of what Navidrome and Roon should see.

**When Subsonic clients send keys.** Then the recoverable copy of the password
can go, and the password forms with it.
