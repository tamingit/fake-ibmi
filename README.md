# Fake IBM i

A scripted stand-in for an IBM i, for testing 5250 clients without a real system. It speaks TN5250E over Telnet and IBM's Network Print host server. Its screens are placeholders, and it keeps nothing.

It was split out of [GreenGlow Connect](https://github.com/tamingit/greenglow), where it ran the server's and browser's tests and the public demo.

**Status:** it serves the clients it was written for. The next goal is enough fidelity that IBM i Access Client Solutions (ACS) can connect to it and behave as it does with a real IBM i.

## What it does

**5250 display (Telnet, port 2323)**

- **Negotiation:** TN5250E, one option at a time as an IBM i does it: NEW-ENVIRON, then the terminal type, then end of record and binary. It asks for a value only once the client has agreed to the option. A client that refuses NEW-ENVIRON still gets a session.
- **Device names:** it honours the device name a client asks for (`DEVNAME`). Without one, it makes one up. A name already in use gets the Startup Response Record's error code, as on a real system.
- **Screens:**
  - **Sign-on:** any user name signs on, and the password is ignored. A blank user gets "User required".
  - **Main menu:**
    - `4`: a list of placeholder items, three pages long, paged with Page Up and Page Down.
    - `132`: a 27 × 132 screen.
    - `90` or `signoff`: ends the session.
    - F3: back to the sign-on screen.
- **Logs:** it logs what clients do, but never the contents of a password field.

**Network Print (host server, port 8474)**

- **Sign-on:** user `PRTUSER`, password `Secret12`. Password levels 0 and 1 use DES; 2 and up use SHA-1.
- **Spooled files:** it lists placeholder spooled files and sends their contents.

## Running it

Docker:

```bash
docker run --rm -p 127.0.0.1:2323:2323 -p 127.0.0.1:8474:8474 ghcr.io/tamingit/fake-ibmi
```

With the .NET 10 SDK:

```bash
dotnet run --project src/FakeIbmI -- --port 2323 --print-port 8474
```

- **Options:**
  - `--port`: the 5250 port (default 2323).
  - `--print-port`: the Network Print port (default: no Network Print server).
  - `--bind`: the address to listen on (default `127.0.0.1`).
  - `--trace`: logs each Telnet negotiation step, and each record's length. It never logs a record's contents, or the value of a NEW-ENVIRON variable that can hold a secret (`IBMSUBSPW`, `IBMRSEED`).
- **Listening address:** by default it listens on loopback only. The image passes `--bind 0.0.0.0` so it can be reached from outside the container. Publish it on `127.0.0.1`, as above, unless you mean other machines to reach it.

## Connecting with IBM i Access Client Solutions

1. Start the fake where the PC running ACS can reach it, with the trace on:
   ```bash
   dotnet run --project src/FakeIbmI -- --port 2323 --bind 0.0.0.0 --trace
   ```
   Or with Docker (arguments after the image name are added to the image's own): `docker run --rm -p 2323:2323 ghcr.io/tamingit/fake-ibmi:beta --trace`.
2. In ACS, make a 5250 session with this machine's address as the system, port 2323, and SSL off.
3. Any user name signs on; the password is ignored.

If it doesn't connect, the trace shows how far the negotiation got:

- **No `connected` line at all:** ACS never reached the fake. Check the address, the port and the firewall.
- **It stops partway:** the last `<-` and `->` lines show which step the client didn't answer.

## Tests

```bash
dotnet test FakeIbmI.slnx
```

- **Client:** the tests drive the fake with the 5250 client from GreenGlow Connect, copied into `tests/FakeIbmI.Tests/Client`.
- **Limitation:** where the fake and that client share code (Telnet, EBCDIC), a mistake made on both sides can't be caught. Checking against other clients, such as ACS, is part of the fidelity work.

GitHub Actions runs the tests and builds the image on every push.

## Releases

Publishing a GitHub release from main runs the tests, then pushes the image to GitHub's container registry:

| Release tag | Image tags |
|---|---|
| `vX.Y.Z-beta` or `vX.Y.Z-beta.N` | `ghcr.io/tamingit/fake-ibmi:X.Y.Z-beta.N` and `:beta` |
| `vX.Y.Z` | `ghcr.io/tamingit/fake-ibmi:X.Y.Z` and `:latest` |

```bash
gh release create v0.1.0-beta.1 --target main --prerelease --generate-notes   # beta
gh release create v0.1.0 --target main --generate-notes                       # stable
```

Other tags, and releases whose commit isn't on main, are refused.
