FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet restore src/GraphRag.Api -r linux-x64 -p:PublishAot=true --locked-mode
RUN dotnet publish src/GraphRag.Api -c Release -r linux-x64 --no-restore -p:PublishAot=true -p:TrimmerSingleWarn=false -o /out

FROM ubuntu:24.04 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl libicu74 libssl3t64 libstdc++6 libgomp1 zlib1g && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1654 app && useradd --uid 1654 --gid 1654 --no-create-home app
WORKDIR /app
COPY --from=build --chown=app:app /out/ .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["/app/GraphRag.Api"]
