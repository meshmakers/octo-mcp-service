FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY ["src/McpServices/McpServices.csproj", "src/McpServices/"]
RUN dotnet restore "src/McpServices/McpServices.csproj" -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE}
COPY . .
WORKDIR "/src/src/McpServices"
ENV NUGET_XMLDOC_MODE=none
RUN dotnet build "./McpServices.csproj" -c $BUILD_CONFIGURATION -o /app/build -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE}

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./McpServices.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.McpServices.dll"]
