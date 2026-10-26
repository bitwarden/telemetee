# Build context is the repository root. Build with deploy/build-image.sh, which sets
# SOURCE_DATE_EPOCH and rewrites layer timestamps so the image is reproducible from source.
#
# Base images are pinned by digest: the hostdata clients pin is a function of every layer, so a
# moving tag would make it impossible to reproduce from the repository alone.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
ARG SOURCE_DATE_EPOCH
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/Telemetee.Collector/Telemetee.Collector.csproj -c Release -o /app -p:ContinuousIntegrationBuild=true

# Chiseled: no shell, no package manager. Keeps the measured image small and the CCE policy's
# exec surface empty.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
WORKDIR /app
COPY --from=build /app .
# 8080 is taken by the attestation sidecar, which shares the container group's network namespace.
ENV ASPNETCORE_HTTP_PORTS=5000
# Disable .NET diagnostics before the runtime starts; the ACI template pins the same values in
# the CCE policy. See docs/decisions.md § Why .NET diagnostics are off in Production.
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_EnableDiagnostics_IPC=0
ENV DOTNET_EnableDiagnostics_Debugger=0
ENV DOTNET_EnableDiagnostics_Profiler=0
EXPOSE 5000
ENTRYPOINT ["dotnet", "Telemetee.Collector.dll"]
