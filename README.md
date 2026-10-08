# ZRevive

**H1Z1: King of the Kill, Preseason 5 gameplay, on community servers.**
Free, non-commercial, and built for people who already own *Z1 Battle Royale* on Steam.

→ **[Download the launcher](https://github.com/ZRevive/ZRevive/releases/latest)** · [zrevive.com](https://zrevive.com)

---

## What it is

ZRevive is a server for the 2019 *Z1 Battle Royale* client that plays like **Preseason 5**
(June–August 2017): the recoil, the bullet drop, the movement speeds, the loot and the crafting
of that era, rebuilt and measured against it rather than guessed.

## How to play

1. Own *Z1 Battle Royale* on Steam. (You do not need to install it from scratch — but the
   launcher does need the files.)
2. Download `ZRevive.exe` from the [latest release](https://github.com/ZRevive/ZRevive/releases/latest)
   and run it. It is self-contained; there is no runtime to install first.
3. Sign in through Steam. The launcher asks for your *Z1 Battle Royale* folder, then builds a
   **separate** ZRevive folder from it. Your Steam copy is never modified.
4. Press PLAY.

> **The servers are currently invite-only.** Sign in at [zrevive.com](https://zrevive.com) and
> the owner approves accounts by hand while we test.

## Nothing here costs money

No shop, no donations, no ads, no paid ranks, no paid cosmetics. **Crowns**, the in-game
currency, are earned only by playing — they cannot be bought and cannot be cashed out.
If anyone ever asks you to pay for ZRevive, it is not us.

## We do not redistribute the game

*H1Z1*, *King of the Kill*, *Z1 Battle Royale* and all of their art, models, sounds and data
belong to **Daybreak Game Company LLC**. ZRevive is not affiliated with, endorsed by, or
sponsored by Daybreak, and **ships none of their files**.

The launcher builds your install from the copy you already own, on your own PC. Where an update
changes a game data sheet, it is applied as a *recipe*: the launcher reads the original rows out
of your own install and rewrites them locally, so the bytes never travel over the network. If you
do not own the game, nothing builds — on purpose.

See [NOTICE.md](NOTICE.md) for the full statement, including how to reach us for a takedown.

## The launcher's source

`launcher/` is the full source of the `ZRevive.exe` you download — the installer, the updater,
the Steam sign-in, the file verification and the UI. You can read exactly what it does to your
PC, and what it does not.

In short: it locates your *Z1 Battle Royale* install, copies it to a separate folder, applies
patches to that copy, and launches it. It never writes to your Steam copy, and it never uploads
anything from your machine.

```
launcher/ZRevive/          the launcher (C#, WPF, .NET 9)
launcher/ZRevive/Install/  install, verify, patch and self-update
launcher/ZRevive.Tests/    373 tests, no network and no game needed to run them
```

Values that belong to one deployment rather than to the source — see `Branding.cs` — are supplied
at build time and are not committed, so a clean clone may need them before everything builds.

## Licence

Our own code is **GPL-3.0** (see [LICENSE](LICENSE)). The login server derives from
[h1emu / h1z1-server](https://github.com/QuentinGruber/h1z1-server), which is GPL-3.0; the game
server, launcher, website and tooling are written by ZRevive.

**Daybreak's content is not covered by that licence and is not ours to license.** Nothing here
grants you any right to it.
