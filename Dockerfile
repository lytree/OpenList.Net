# syntax=docker/dockerfile:1.7

# === Stage 1: Fetch pre-built non-lite frontend dist from upstream OpenList-Frontend ===
# The upstream release ships a ready-to-serve dist tarball (index.html at root), so no
# Node.js toolchain is needed. Default FRONTEND_VERSION pins a known-good tag; CI passes
# the resolved latest tag via --build-arg FRONTEND_VERSION=...
FROM alpine:3.20 AS frontend-dist
ARG FRONTEND_VERSION=v4.2.2
WORKDIR /fe
RUN apk add --no-cache curl tar \
 && curl -fsSL -o dist.tar.gz \
      "https://github.com/OpenListTeam/OpenList-Frontend/releases/download/${FRONTEND_VERSION}/openlist-frontend-dist-${FRONTEND_VERSION}.tar.gz" \
 && mkdir -p /dist \
 && tar -xzf dist.tar.gz -C /dist \
 && rm dist.tar.gz \
 && printf '%s' "${FRONTEND_VERSION}" > /dist/VERSION

# === Stage 2: Restore + publish .NET server ===
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + central build files first for layer caching
COPY OpenList.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .

# Copy ALL csproj referenced by the solution (Drivers was previously missing,
# which broke `dotnet restore` on the .slnx).
COPY src/OpenList.Core/OpenList.Core.csproj src/OpenList.Core/
COPY src/OpenList.Drivers/OpenList.Drivers.csproj src/OpenList.Drivers/
COPY src/OpenList.Infrastructure/OpenList.Infrastructure.csproj src/OpenList.Infrastructure/
COPY src/OpenList.Server/OpenList.Server.csproj src/OpenList.Server/
RUN dotnet restore

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/OpenList.Server/OpenList.Server.csproj -c Release -o /app/publish

# === Stage 3: Runtime image ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ICU for globalization (upstream Dockerfile convention)
RUN apt-get update && apt-get install -y --no-install-recommends libicu-dev \
 && rm -rf /var/lib/apt/lists/*

# .NET publish output
COPY --from=build /app/publish .

# Frontend dist → /frontend/dist.
# DistDir default ("../../frontend/dist" relative to /app) resolves here, but we
# also set it explicitly via env to be unambiguous.
COPY --from=frontend-dist /dist /frontend/dist

RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://0.0.0.0:5244
ENV OpenList__DistDir=/frontend/dist
EXPOSE 5244

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "OpenList.Server.dll"]
