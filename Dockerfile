FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY OpenList.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY src/OpenList.Core/OpenList.Core.csproj src/OpenList.Core/
COPY src/OpenList.Infrastructure/OpenList.Infrastructure.csproj src/OpenList.Infrastructure/
COPY src/OpenList.Server/OpenList.Server.csproj src/OpenList.Server/
RUN dotnet restore

# Copy source and build
COPY src/ src/
RUN dotnet publish src/OpenList.Server/OpenList.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install ICU for internationalization
RUN apt-get update && apt-get install -y libicu-dev && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Create data directory
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://0.0.0.0:5244
EXPOSE 5244

VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "OpenList.Server.dll"]
