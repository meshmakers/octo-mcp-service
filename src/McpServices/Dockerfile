FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8443

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/McpServices"
RUN dotnet build "./McpServices.csproj" -o /app/build -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE}

FROM build AS publish
RUN dotnet publish "./McpServices.csproj" -o /app/publish /p:UseAppHost=false -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.McpServices.dll"]
