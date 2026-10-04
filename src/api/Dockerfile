# Build from the repository root: docker build -f src/api/Dockerfile .
# The SDK stage runs natively on the build machine and cross-publishes for the target arch (no QEMU);
# without BuildKit TARGETARCH is empty and the publish is the portable default.
# WITH_CLAUDE=true adds Node 22 LTS and the Claude Code CLI for headless Claude reviews (#166, about +480 MB);
# the default false builds the image without them.
ARG WITH_CLAUDE=false

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Release version from the tag (publish.yml passes it without the leading v); local builds stay 0.0.0-dev.
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/api/GmailOrganiser.Api.csproj src/api/
RUN dotnet restore src/api/GmailOrganiser.Api.csproj ${TARGETARCH:+-a $TARGETARCH}
COPY src/api/ src/api/
RUN dotnet publish src/api/GmailOrganiser.Api.csproj -c Release -o /app/publish --no-restore \
    -p:Version=$VERSION ${TARGETARCH:+-a $TARGETARCH}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Local OCR for image attachments and scanned PDFs (#72): Tesseract (Apache-2.0) with English, about +55 MB.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng \
    && rm -rf /var/lib/apt/lists/*

FROM runtime-base AS claude-false

# Node 22 LTS from NodeSource and the CLI (pinned major). The CLI keeps its state (~/.claude, ~/.claude.json) in HOME,
# which must be writable by $APP_UID; the image's own version is kept, so the auto-updater is off.
FROM runtime-base AS claude-true
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code@2 \
    && npm cache clean --force \
    && apt-get purge -y curl gnupg && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
ENV HOME=/home/app DISABLE_AUTOUPDATER=1
RUN mkdir -p /home/app && chown -R $APP_UID:$APP_UID /home/app

FROM claude-${WITH_CLAUDE} AS runtime
COPY --from=build /app/publish .
# Data Protection key ring; mount the dp-keys volume here (the empty dir's ownership is copied into a new volume).
ENV DataProtection__KeysPath=/keys
RUN mkdir -p /keys && chown $APP_UID /keys
USER $APP_UID
ENTRYPOINT ["dotnet", "GmailOrganiser.Api.dll"]
