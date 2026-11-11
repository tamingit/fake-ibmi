# Fake IBM i

A scripted stand-in for an IBM i, for testing 5250 clients without a real system. It speaks TN5250E over Telnet, and four of IBM's host servers: the port mapper, sign-on, remote command and Network Print. All but the port mapper can also listen for TLS. Its screens are placeholders, and it keeps nothing.

It was split out of [GreenGlow Connect](https://github.com/tamingit/greenglow), where it ran the server's and browser's tests and the public demo.

**Status:** it serves the clients it was written for. The next goal is enough fidelity that IBM i Access Client Solutions (ACS) can connect to it and behave as it does with a real IBM i.

## What it does

**5250 display (Telnet, port 2323)**

- **Negotiation:** TN5250E, one option at a time as an IBM i does it: NEW-ENVIRON, then the terminal type, then end of record and binary. It asks for a value only once the client has agreed to the option. A client that refuses NEW-ENVIRON still gets a session.
- **Device names:** it honours the device name a client asks for (`DEVNAME`). Without one, it makes one up. A name already in use gets the Startup Response Record's error code, as on a real system.
- **Screens:**
  - **Sign-on:** checks the user and password against the [users](#users), with an IBM i's messages for an unknown user, a wrong password or a disabled profile. The password field is case-sensitive.
  - **Change Password:** the expired user goes on to this screen, which needs the current password and the new one twice. F3 or F12 goes back to sign-on.
  - **Main menu:**
    - `4`: a list of placeholder items, three pages long, paged with Page Up and Page Down.
    - `132`: a 27 × 132 screen.
    - `90` or `signoff`: ends the session.
    - F3: back to the sign-on screen.
- **Logs:** it logs what clients do, but never the contents of a password field.

**Host servers**

Password levels 0 and 1 use DES; 2 and 3 use SHA-1. The fake runs at level 2.

- **Port mapper (port 449):** tells a client which port a service is on, as JTOpen and ACS ask before connecting to a host server. It knows `as-signon`, `as-rmtcmd` and `as-netprt` when they run, and answers that there's no such service for anything else, including the TLS names (`as-signon-s`). It logs each name asked, so the log shows which services a client wants.
- **Sign-on (port 8476):**
  - **Sign-on:** checks the user and password, and answers with the sign-on dates and the server's CCSID (37). It claims to be IBM i 7.5.
  - **Change password:** checks the old password, and the new one: it can't be empty, too long, or the same as the old one.
  - **Not supported:** profile tokens, Kerberos and multi-factor authentication.
- **Remote command (port 8475):** runs CL commands and calls programs, as ACS does after signing on. It runs nothing: every command succeeds with no messages, and every program call succeeds. A few programs get real answers:
  - `QSYCKUFU` (Check User Function Usage): every function is allowed for every user. ACS checks its functions this way.
  - `QWCRTVCA` (Retrieve Current Attributes): the server job's name.
  - **Any other program:** its output parameters come back filled with zeros. For an IBM API, zeros in the error code parameter mean success.

  The log names each command and program, with the program's parameter sizes and, for `QSYCKUFU`, the function checked. It never logs a command's parameters or other programs' data, which can hold passwords.
- **Network Print (port 8474):** it lists placeholder spooled files and sends their contents.

<a id="users"></a>**Users**

Every server checks sign-ons against the same three users: the 5250 sign-on screen, the sign-on server and Network Print. A password changed through any of them holds for all of them, until the fake stops.

| User | Password | Signing on with the right password |
|---|---|---|
| `PRTUSER` | `Secret12` | Signs on. |
| `EXPUSER` | `Expired1` | The password has expired. The 5250 screen goes on to Change Password; the sign-on server answers `0003000D` until the password is changed through it. |
| `LOCKUSER` | `Locked12` | The profile is disabled, so it can't sign on with any password. The 5250 screen says "User profile LOCKUSER cannot sign on."; the host servers answer `00020002`. |

- **Setting them:** copy `.env.example` to `.env` and change the names and passwords. The fake reads `.env` from the directory it starts in, or the file `--env-file` names. Variables set in the environment win over the file. For Docker, pass the file with `--env-file .env`, or leave that out to use the defaults.
- **Values:** write them without quotes, as Docker keeps quotes as part of the value. A user name is up to 10 characters. A password can be up to 27 characters on the 5250 sign-on screen, and up to 128 for the host servers.
- **Logs:** the fake logs who signed on and why a sign-on was refused, but never a password.

**TLS**

As on an IBM i, TLS is a second port for the same server: Telnet on 992, sign-on on 9476, remote command on 9475, Network Print on 9474. A TLS session shares everything with a session on the plain port: device names, and the users. The port mapper stays unencrypted on 449, and tells clients the TLS ports when they ask for `as-signon-s`, `as-rmtcmd-s` or `as-netprt-s`.

- **Certificate:** `--tls-cert` names a PKCS #12 file without a password. If the file doesn't exist, the fake makes a self-signed certificate there, good for 397 days, and writes its public part beside it as PEM (the same name, ending `.crt`) for clients to trust. Without `--tls-cert`, it makes a certificate that lasts only until it stops.
- **Names:** a certificate the fake makes is for `localhost`, `127.0.0.1`, `::1`, the machine's name and the `--bind` address. `--tls-name` adds more, such as the name or address clients connect to.
- **Not supported:** client certificates.
- **Logs:** each TLS connection logs its TLS version, or why the handshake failed. A client that doesn't trust the certificate shows up as `certificate unknown`.

## Running it

Docker:

```bash
docker run --rm --env-file .env -v fake-ibmi-certs:/certs \
  -p 127.0.0.1:2323:2323 -p 127.0.0.1:992:992 -p 127.0.0.1:8474:8474 -p 127.0.0.1:9474:9474 \
  -p 127.0.0.1:8475:8475 -p 127.0.0.1:9475:9475 -p 127.0.0.1:8476:8476 -p 127.0.0.1:9476:9476 \
  -p 127.0.0.1:449:449 ghcr.io/tamingit/fake-ibmi
```

- **Ports:** publish each port on the same number: the port mapper tells clients the ports inside the container.
- **Certificate:** the image keeps its certificate in `/certs`. The named volume keeps it from one run to the next, so clients only need to trust it once. Copy out the part to trust with `docker cp <container>:/certs/fake-ibmi.crt .`. Use a named volume rather than a directory: the fake runs as a non-root user, which can't write to a directory you mount.

With the .NET 10 SDK:

```bash
dotnet run --project src/FakeIbmI -- --port 2323 --print-port 8474 --signon-port 8476 --command-port 8475 --mapper-port 449
```

Port 449 is below 1024, so outside a container the fake needs root, or `CAP_NET_BIND_SERVICE`, to listen there.

- **Options:**
  - `--port`: the 5250 port (default 2323).
  - `--print-port`: the Network Print port (default: no Network Print server).
  - `--signon-port`: the sign-on port (default: no sign-on server).
  - `--command-port`: the remote command port (default: no remote command server).
  - `--mapper-port`: the port mapper's port (default: no port mapper). Clients only ever look for it on 449.
  - `--tls-port`, `--signon-tls-port`, `--command-tls-port`, `--print-tls-port`: TLS ports for Telnet, sign-on, remote command and Network Print (default: none). Each but the first needs its server's plain port too.
  - `--tls-cert`: the certificate for the TLS ports (default: one made for this run only).
  - `--tls-name`: another name or address for a certificate the fake makes. Repeat it, or separate names with commas.
  - `--env-file`: the file of settings, such as the [users](#users) (default: `.env`, when there is one).
  - `--bind`: the address to listen on (default `127.0.0.1`).
  - `--trace`: logs each Telnet negotiation step, and each record's length. It never logs a record's contents, or the value of a NEW-ENVIRON variable that can hold a secret (`IBMSUBSPW`, `IBMRSEED`).
- **Listening address:** by default it listens on loopback only. The image passes `--bind 0.0.0.0` so it can be reached from outside the container. Publish it on `127.0.0.1`, as above, unless you mean other machines to reach it.

## Connecting with IBM i Access Client Solutions

ACS doesn't only use the 5250 port. It signs on to the sign-on server first, for example to check the password or to bypass the 5250 sign-on screen, and it finds that server through the port mapper on port 449. After signing on, it uses the remote command server. So the fake needs all four: Telnet, sign-on, remote command and the port mapper.

1. Start the fake where the PC running ACS can reach it, with the trace on. Docker is easiest, because it can listen on 449 without root (arguments after the image name are added to the image's own):
   ```bash
   docker run --rm --env-file .env -p 2323:2323 -p 8474:8474 -p 8475:8475 -p 8476:8476 -p 449:449 ghcr.io/tamingit/fake-ibmi:beta --trace
   ```
   Or, as root or with `CAP_NET_BIND_SERVICE`:
   ```bash
   dotnet run --project src/FakeIbmI -- --port 2323 --signon-port 8476 --command-port 8475 --mapper-port 449 --bind 0.0.0.0 --trace
   ```
2. In ACS, make a 5250 session with this machine's address as the system, port 2323, and SSL off. For TLS, use port 992 with SSL on, and turn on SSL in the system's connection settings too. ACS must trust the fake's certificate first: import `fake-ibmi.crt` into ACS's key store, under Tools, then Key Management. If ACS connects by a name or address the certificate isn't for, add it with `--tls-name` and make a new certificate.
3. Sign on as one of the [users](#users), both when ACS asks for a user and password and on the 5250 sign-on screen.

If it doesn't connect, the log shows how far ACS got:

- **`mapper` lines:** each service ACS asked the port mapper for. A service the fake doesn't run says so; that's what ACS wanted next.
- **`signon` lines:** whether ACS signed on, or why it was refused.
- **`rmtcmd` lines:** each command ACS ran and program it called. The fake answers them all with success; if ACS still stops, the last one is the likeliest to need a real answer.
- **`TLS handshake failed`:** with `certificate unknown`, ACS doesn't trust the certificate yet.
- **`as-hostcnn-s: no such service here`:** recent clients ask for the host connection server, used for multi-factor authentication. They carry on without it.
- **No `connected` line at all:** ACS never reached the 5250 server. Check the address, the port and the firewall.
- **It stops partway:** the last `<-` and `->` lines show which step the client didn't answer.

## Tests

```bash
dotnet test FakeIbmI.slnx
```

- **Client:** the tests drive the fake with the 5250 client from GreenGlow Connect, copied into `tests/FakeIbmI.Tests/Client`.
- **Sign-on server:** the tests send requests laid out as JTOpen 21.0.7 builds them. The password substitutes and protected passwords in them were computed by JTOpen itself, so they don't share the fake's password code.
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
