# HexBridge automatic updates

Two platforms, two mechanisms, one promise: **check once a day, install only on the
user's command, switch it off with a single toggle.**

| | macOS | Windows |
|---|---|---|
| Mechanism | Sparkle 2.9.6 | Velopack 1.2.0 |
| Channel | `appcast.xml` — a release asset | the repository's GitHub Releases |
| Signature | EdDSA (ed25519) on top of the code signature | installer signature (none yet) |
| Toggle | Settings → General → Updates | Settings → UPDATES |
| Interval | `SUScheduledCheckInterval` = 86400 | `UpdatePolicy.CheckInterval` = 1 day |

Both halves hold the same interval, and that is enforced by a test
(`UpdatePolicyTests.TheIntervalIsADay`) rather than by memory.

---

## macOS: Sparkle

### Why this works at all on an ad-hoc signature

HexBridge is **ad-hoc signed**, not signed with a Developer ID certificate: the
project has no paid certificate. Sparkle does not need one. Sparkle verifies the
**EdDSA signature of the archive** against the `SUPublicEDKey` from `Info.plist`,
and that check does not depend on the code signature — which is exactly what makes
it possible to update an unsigned or ad-hoc signed build.

What is lost without a Developer ID: notarization. On first launch the user still
has to go through "Open Anyway" in the security settings. That has not changed, and
updates do not fix it.

### The microphone prompt comes back — and that has to be said in advance

An ad-hoc signature has no stable identity: the code hash changes with every build,
so after an update macOS considers the app **new** and TCC asks for microphone
access again.

Nothing broke and nothing was reset: the PC's address and the pairing key live in
`~/Library/Application Support/HexBridge/config.json` and updates never touch them.
But someone who was not told this reads a permission prompt as a failure. So the
text is shown **before** the update rather than after, in two places:

* in Settings, next to the toggle — `Wording.updateWillReaskForMicrophone`;
* in Sparkle's own window — the same paragraph sits in the `<description>` of the
  appcast item that `mac/scripts/make-appcast.sh` generates.

### The signing key

The key is ed25519. The private half **never** goes into the repository: with it,
anyone can sign an "update" to HexBridge, and Sparkle will install it silently,
because the signature checks out.

**Where it lives today:**

```
secrets/sparkle_ed25519_private_key      # private, 0600, in .gitignore
secrets/sparkle_ed25519_public_key       # public, the same one as in Info.plist
```

The public key of this build:

```
dCeQPn6bc2CeCgLpffrSITwjLydswffxxHQGvjPbnUI=
```

It is baked into `mac/scripts/build-app.sh` as the default for
`SPARKLE_PUBLIC_KEY` and ends up in `Info.plist` at build time.

**How to put the private key into GitHub Actions:**

```bash
gh secret set SPARKLE_PRIVATE_KEY < secrets/sparkle_ed25519_private_key
```

or by hand: Settings → Secrets and variables → Actions → New repository secret,
name `SPARKLE_PRIVATE_KEY`, value — **the entire contents of the file** (one line
of base64, 44 characters). The `release.yml` workflow writes it to a temporary
file, hands it to `sign_update` and deletes it via `trap` — including when the
build fails.

Without that secret the release **fails deliberately**, with a message in the log.
Shipping an unsigned appcast silently is not an option: Sparkle would reject it,
updates would simply stop arriving, and nobody would notice.

**If the key ever has to be regenerated** (leaked, lost):

```bash
openssl genpkey -algorithm ed25519 -out /tmp/k.pem
openssl pkey -in /tmp/k.pem  -outform DER -out /tmp/k.der
openssl pkey -in /tmp/k.pem -pubout -outform DER -out /tmp/kp.der
python3 - <<'PY'
import base64, pathlib
print("private:", base64.b64encode(pathlib.Path("/tmp/k.der").read_bytes()[-32:]).decode())
print("public: ", base64.b64encode(pathlib.Path("/tmp/kp.der").read_bytes()[-32:]).decode())
PY
rm -f /tmp/k.pem /tmp/k.der /tmp/kp.der
```

Sparkle's private key file format is **base64 of a 32-byte seed**, exactly what the
script above prints. (Sparkle also accepts the old 96-byte format; the new one is
the seed.) `bin/generate_keys` does the same thing but puts the key in the Keychain,
which is useless for CI and, on a machine with a locked Keychain, hangs silently.

Rotating the key means **builds already installed stop updating**: their
`Info.plist` has the old public key. Those users download the new version by hand,
once.

### How the appcast is built

```bash
mac/scripts/make-appcast.sh <version> <zip> <URL where the zip will live> [output]
```

The feed contains **exactly one item** — the release being published. That is not a
simplification but a consequence of where the feed lives: `SUFeedURL` points at

```
https://github.com/HexArchy/hexbridge/releases/latest/download/appcast.xml
```

and GitHub resolves `latest/download/…` to an asset of the newest release. So every
release carries its own feed, the URL never changes, and there is no file that has
to be hand-edited and re-signed on every ship.

---

## Windows: Velopack

Velopack was chosen not by taste but for one decisive reason: **it builds the
installer and the update channel straight from macOS.**

```bash
vpk '[win]' pack --packId HexBridge --packVersion 1.2.0 \
    --packDir publish/win --mainExe HexBridge.exe -r win-x64 -o velopack
```

The `[win]` directive turns on cross-compilation, and the release workflow needs no
Windows runner. Verified on this machine: `Directive enabled for cross-compiling
from OSX (current os) to Windows`, producing `HexBridge-win-Setup.exe`,
`*-full.nupkg`, `RELEASES` and `releases.win.json`.

### `VelopackApp.Build().Run()` — on the first line

This is not a style preference. Velopack relaunches the executable with internal
arguments (`--veloapp-install` and relatives) during install, update and uninstall.
`Run()` recognizes them, does its job and exits the process. Anything **above** it
runs on every one of those invisible launches — and a window opened up there is the
classic "the installer flashed a UI" bug.

`vpk` checks this itself and prints to the log:
`Verified VelopackApp.Run() in 'System.Void HexBridge.App.Program::Main(System.String)'`.

### The portable build

The zip with the unpacked receiver, the one the user carries over to the gaming PC
by hand, stays as it was and cannot update itself. The app knows this
(`UpdateViewModel.IsSupported` reads `UpdateManager.IsInstalled`) and does not show
a button that would not work anyway.

### Installer signing

There is none yet — `vpk` warns about it honestly: `No signing parameters provided`.
SmartScreen will complain about the first launch of the installer, as it would about
any other unsigned exe. A code signing certificate fixes it; when one exists, it goes
in as a `--signParams` flag and nothing else in the pipeline changes.

---

## What happens on a release

`.github/workflows/release.yml`, tag `v*`:

1. **macos** — Swift tests, build the `.app` with the version from the tag, zip it,
   build the appcast signed with the `SPARKLE_PRIVATE_KEY` secret.
2. **windows** — .NET tests and the portable zip, as before.
3. **velopack** — on the macOS runner: `dotnet publish -r win-x64`, then `vpk pack`.
4. **relay** — as before.
5. **publish** — gathers everything, computes checksums (except for the appcast,
   which is signed in its own right) and **checks that the update channels are
   there** — `appcast.xml`, `HexBridge-win-Setup.exe`, `RELEASES`,
   `releases.win.json`. If any is missing the release fails: updates that silently
   do not work are worse than no updates.

---

## What has not been verified

* **A real update has never run end to end.** That needs two published releases and
  an installation of the first. What has been verified piecewise: the signature and
  its verification with the same key that is in `Info.plist`; parsing the appcast as
  XML; the presence of `Sparkle.framework` in the bundle and that `dyld` finds it;
  building the Velopack package from macOS.
* **SmartScreen and Gatekeeper** on freshly downloaded artifacts — only on a real
  user's machine.
* **Sparkle delta updates** (`BinaryDelta`) are not used: they require the previous
  archive on the runner. The user downloads the full zip, which is 2 MB.
