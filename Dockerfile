# The fake IBM i:  docker build -t fake-ibmi .
#   docker run --rm -p 127.0.0.1:2323:2323 -p 127.0.0.1:8474:8474 -p 127.0.0.1:8476:8476 -p 127.0.0.1:449:449 fake-ibmi
# 5250 Telnet on 2323; Network Print, sign-on and the port mapper on IBM's own ports: 8474, 8476
# and 449. The mapper tells clients the ports inside the container, so publish them unchanged.
# (Docker lets a non-root process in a container listen below 1024.)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FakeIbmI/FakeIbmI.csproj src/FakeIbmI/
RUN dotnet restore src/FakeIbmI/FakeIbmI.csproj
COPY src/ src/
RUN dotnet publish src/FakeIbmI/FakeIbmI.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
USER app
EXPOSE 2323 8474 8476 449
ENTRYPOINT ["dotnet", "FakeIbmI.dll", "--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474", "--signon-port", "8476", "--mapper-port", "449"]
