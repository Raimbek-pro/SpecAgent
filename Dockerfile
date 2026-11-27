# Multi-stage build for SpecAgent (.NET 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for optimal Docker layer caching
COPY doc1/prototype/src/SpecAgent.Core/SpecAgent.Core.csproj doc1/prototype/src/SpecAgent.Core/
COPY doc1/prototype/src/SpecAgent.Ingestion/SpecAgent.Ingestion.csproj doc1/prototype/src/SpecAgent.Ingestion/
COPY doc1/prototype/src/SpecAgent.Agents/SpecAgent.Agents.csproj doc1/prototype/src/SpecAgent.Agents/
COPY doc1/prototype/src/SpecAgent.Api/SpecAgent.Api.csproj doc1/prototype/src/SpecAgent.Api/

RUN dotnet restore doc1/prototype/src/SpecAgent.Api/SpecAgent.Api.csproj

# Copy source code and publish
COPY doc1/prototype/src/ doc1/prototype/src/
RUN dotnet publish doc1/prototype/src/SpecAgent.Api/SpecAgent.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install DejaVu Sans fonts for crisp Cyrillic PDF generation (QuestPDF)
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY doc1/prototype/knowledge ./knowledge

RUN mkdir -p /app/uploads && chown -R $APP_UID /app/uploads /app/knowledge

ARG SPECAGENT_VERSION=""
ENV ASPNETCORE_URLS=http://+:8080
ENV Knowledge__Path=/app/knowledge
ENV SPECAGENT_VERSION=${SPECAGENT_VERSION}

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "SpecAgent.Api.dll"]
