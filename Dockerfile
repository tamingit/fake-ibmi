# The fake IBM i:  docker build -t fake-ibmi .
#   docker run --rm -p 127.0.0.1:2323:2323 -p 127.0.0.1:8474:8474 -p 127.0.0.1:8475:8475 -p 127.0.0.1:8476:8476 -p 127.0.0.1:449:449 fake-ibmi
# 5250 Telnet on 2323; Network Print, remote command, sign-on and the port mapper on IBM's own
# ports: 8474, 8475, 8476 and 449. The mapper tells clients the ports inside the container, so
# publish them unchanged.
# (Docker lets a non-root process in a container listen below 1024.)
# TLS on IBM's ports too: Telnet 992, Network Print 9474, remote command 9475, sign-on 9476. The certificate is made
# in /certs on first start; mount a named volume there (-v fake-ibmi-certs:/certs) to keep it,
# and copy /certs/fake-ibmi.crt out for clients to trust.
# Each library's SQLite database (source, programs, tables) is a file in /data (QGPL.db, MYLIB.db ...), and QGPL's also
# keeps the users and spooled files: mount a named volume on /data (-v fake-ibmi-data:/data) to keep them.
# To load source, mount a folder with a folder for each library and set FAKE_IBMI_SOURCE_DIR to it
# (-v "$PWD/source:/source:ro" -e FAKE_IBMI_SOURCE_DIR=/source).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FakeIbmI/FakeIbmI.csproj src/FakeIbmI/
RUN dotnet restore src/FakeIbmI/FakeIbmI.csproj
COPY src/ src/
RUN dotnet publish src/FakeIbmI/FakeIbmI.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir /certs /data && chown app /certs /data
USER app
EXPOSE 2323 992 8474 9474 8475 9475 8476 9476 449
ENTRYPOINT ["dotnet", "FakeIbmI.dll", "--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474", "--signon-port", "8476", "--command-port", "8475", "--mapper-port", "449", \
    "--tls-port", "992", "--print-tls-port", "9474", "--signon-tls-port", "9476", "--command-tls-port", "9475", "--tls-cert", "/certs/fake-ibmi.pfx", "--database-dir", "/data"]
