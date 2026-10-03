# Build from the repository root: docker build -f src/api/Dockerfile .
# The SDK stage runs natively on the build machine and cross-publishes for the target arch (no QEMU);
# without BuildKit TARGETARCH is empty and the publish is the portable default.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/api/GmailOrganiser.Api.csproj src/api/
RUN dotnet restore src/api/GmailOrganiser.Api.csproj ${TARGETARCH:+-a $TARGETARCH}
COPY src/api/ src/api/
RUN dotnet publish src/api/GmailOrganiser.Api.csproj -c Release -o /app/publish --no-restore \
    ${TARGETARCH:+-a $TARGETARCH}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Local OCR for image attachments and scanned PDFs (#72): Tesseract (Apache-2.0) with English, about +55 MB.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
# Data Protection key ring; mount the dp-keys volume here (the empty dir's ownership is copied into a new volume).
ENV DataProtection__KeysPath=/keys
RUN mkdir -p /keys && chown $APP_UID /keys
USER $APP_UID
ENTRYPOINT ["dotnet", "GmailOrganiser.Api.dll"]
