FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json NightElf.slnx ./
COPY src/ src/
COPY contract/ contract/
COPY protobuf/ protobuf/

RUN dotnet restore NightElf.slnx
RUN dotnet publish src/NightElf.Launcher/NightElf.Launcher.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5005
ENV NightElf__Launcher__ApiPort=5005
ENV NightElf__Launcher__DataRootPath=/data
ENV NightElf__Launcher__CheckpointRootPath=/data/checkpoints

EXPOSE 5005
VOLUME ["/data"]

HEALTHCHECK --interval=10s --timeout=3s --retries=3 \
    CMD curl -f http://localhost:5005/health || exit 1

ENTRYPOINT ["dotnet", "NightElf.Launcher.dll"]
